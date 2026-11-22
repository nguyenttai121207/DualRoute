using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DualRoute.Client.Windows;

internal sealed record AdapterIdentity(Guid Guid, string Name, string InstanceId);
internal interface IAdapterSystem
{
    IReadOnlyList<AdapterIdentity> Find();
    void Remove(AdapterIdentity adapter);
    Task ConfigureAsync(ulong luid, System.Net.IPAddress ip, CancellationToken ct);
}

internal sealed record WintunState(int Version, string Owner, Guid AdapterGuid, string Name,
    Guid OperationId, string Stage, string? InstanceId);

/// <summary>Adapter-only journal. Never reads or removes routes or the shared Wintun driver.</summary>
internal sealed class WintunRecovery : IDisposable
{
    internal static readonly Guid AdapterGuid = new("25a5019d-1957-4cd9-81d1-44faaf488e9d");
    internal static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DualRoute", "Wintun");
    private readonly string path;
    private readonly FileStream lease;
    private readonly IAdapterSystem system;
    private readonly bool secure;
    private WintunState? state;

    internal WintunRecovery(string directory, IAdapterSystem system, bool secure = true)
    {
        this.system = system;
        this.secure = secure;
        if (secure)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            security.SetOwner(administrators);
            // Protect the app parent too: a writable parent permits replacement of the journal directory.
            if (Path.GetFullPath(directory).Equals(Path.GetFullPath(DefaultDirectory), StringComparison.OrdinalIgnoreCase))
                SecureDirectory(Directory.GetParent(directory)!.FullName, security);
            SecureDirectory(directory, security);
        }
        else Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "wintun-state.json");
        var lockPath = Path.Combine(directory, "wintun.lock");
        foreach (var file in new[] { path, lockPath })
            if (File.Exists(file))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unsafe Wintun journal/lock link.");
                if (secure) ValidateSecurity(new FileInfo(file).GetAccessControl());
            }
        lease = OpenFile(lockPath, FileMode.OpenOrCreate, FileOptions.None);
    }

    private FileStream OpenFile(string file, FileMode mode, FileOptions options)
    {
        if (!secure) return new FileStream(file, mode, FileAccess.ReadWrite, FileShare.None, 4096, options);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.SetOwner(administrators);
        foreach (var sid in new[] { administrators, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(file).Create(mode, FileSystemRights.FullControl, FileShare.None, 4096, options, security);
    }

    private static void SecureDirectory(string path, DirectorySecurity security)
    {
        for (var ancestor = new DirectoryInfo(path); ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Wintun journal path must not traverse reparse points.");
        var info = new DirectoryInfo(path);
        if (!info.Exists) info.Create(security);
        ValidateSecurity(info.GetAccessControl());
    }
    private static void ValidateSecurity(FileSystemSecurity existing)
    {
        var owner = existing.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (owner != admins && owner != system)
            throw new IOException("Untrusted Wintun journal directory owner; no adapter was removed.");
        foreach (FileSystemAccessRule rule in existing.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !rule.IdentityReference.Equals(admins) && !rule.IdentityReference.Equals(system) &&
                (rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                    FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0)
                throw new IOException("Wintun journal directory permits untrusted writes; no adapter was removed.");
    }

    internal async Task RecoverAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 16384)
            throw new IOException("Unsafe Wintun journal; no adapter was removed.");
        try { state = JsonSerializer.Deserialize<WintunState>(await File.ReadAllTextAsync(path, ct)); }
        catch (JsonException ex) { throw new IOException("Invalid Wintun journal; no adapter was removed. Inspect the state file.", ex); }
        if (state is null || state.Version != 1 || state.Owner != "DualRoute.Wintun" || state.AdapterGuid != AdapterGuid ||
            state.OperationId == Guid.Empty || state.Name != "DualRoute-" + state.OperationId.ToString("N") ||
            state.Stage is not ("Creating" or "Created") || (state.Stage == "Created" && string.IsNullOrEmpty(state.InstanceId)))
            throw new IOException("Unknown Wintun ownership/schema; no adapter was removed.");
        var matches = system.Find().Where(a => a.Guid == state.AdapterGuid || a.Name == state.Name).ToArray();
        if (matches.Any(a => !Owns(a)) || matches.Length > 1)
            throw new IOException("Wintun identity conflicts with journal; no adapter was removed.");
        foreach (var adapter in matches) { ct.ThrowIfCancellationRequested(); system.Remove(adapter); }
        await CompleteAsync(ct);
    }

    private bool Owns(AdapterIdentity a) => state is not null && a.Guid == state.AdapterGuid && a.Name == state.Name &&
        (state.InstanceId is null || a.InstanceId == state.InstanceId);

    internal void Begin()
    {
        if (system.Find().Any(a => a.Guid == AdapterGuid)) throw new IOException("DualRoute adapter GUID is already in use; refusing to adopt it.");
        var id = Guid.NewGuid();
        state = new(1, "DualRoute.Wintun", AdapterGuid, "DualRoute-" + id.ToString("N"), id, "Creating", null);
        Save();
    }
    internal string Name => state?.Name ?? throw new InvalidOperationException("Journal not started.");
    internal void Created()
    {
        var adapter = system.Find().SingleOrDefault(a => Owns(a)) ?? throw new IOException("Cannot verify newly created DualRoute adapter identity.");
        state = state! with { Stage = "Created", InstanceId = adapter.InstanceId };
        Save();
    }
    private void Save()
    {
        // Flush the replacement before atomic rename; the prior valid state survives an interrupted write.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = OpenFile(temp, FileMode.CreateNew, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, state); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal async Task CompleteAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (system.Find().Any(a => a.Guid == AdapterGuid))
            await Task.Delay(50, deadline.Token);
        File.Delete(path);
        state = null;
    }
    public void Dispose() => lease.Dispose();
}
