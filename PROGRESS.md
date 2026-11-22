# DualRoute — Tiến độ dự án

Cập nhật: 2026-11-22. Nguồn phạm vi: MVP_SCOPE_v3.md; quy tắc triển khai: AGENTS.md. Không ghi PSK/cấu hình bí mật vào nhật ký này.

## Lịch sử đã hoàn thành

| Mốc | Kết quả và nguồn xác nhận |
|---|---|
| MVP chẩn đoán ICMP | GameNetworkOptimizer: gateway/public IP, RTT min/max/average/P95, jitter, loss ICMP, spike, CSV UTF-8, Ctrl+C; 12 tests. Đây chưa phải Test Mode tích hợp của Phase 4. |
| Phase 1 — a495f27 | Thư viện net10.0 riêng, UDP thật và truyền tải giả, nhiều client, handshake, PROBE/KEEPALIVE/DISCONNECT, bounds, replay; lúc đó tổng 37 tests pass Windows. |
| Tài liệu — c8c524d | Việt hóa README, mô tả kiến trúc/giao thức, lệnh build/test và giới hạn đã kiểm chứng. |
| Quy tắc/phạm vi — f67c2b0 | AGENTS.md và MVP_SCOPE_v3.md được đưa vào Git; thống nhất giao thức Phase 1 và MTU 1350. |
| Bản vá heartbeat — 34cc6f3 | Khởi động heartbeat khi null hoặc IsCompleted. Thêm test mất mạng → Failed → ConnectAsync → duy trì phiên vượt IdleTimeout. Tổng 38/38 tests pass Windows. |
| phase1-verified | Mốc Git tag có trong checkout. Người dùng xác nhận nền tảng 38/38 tests đã pass cả Windows và Ubuntu LAN 192.168.1.200. Lượt triển khai 2A không chạy lại Ubuntu; không dùng kết quả Phase 1 để khẳng định TUN thật đã hoạt động. |
| Hạ tầng Linux LAN | Theo xác nhận của người dùng: Ubuntu 192.168.1.200, UDP 5000, NIC enp2s0; ip_forward=1, iptables MASQUERADE và ufw route allow đã mở sẵn. Khi test thực tế vẫn phải kiểm tra lại, không giả định hoặc tự sửa. |

### Giao thức Phase 1 đã chốt

- Header: version | type | client_id (UUID 16 byte) | session_id (8 byte) | seq (8 byte), big-endian; header 34 byte.
- HELLO/HELLO_ACK xác thực HMAC-SHA256; client_nonce/server_nonce mỗi loại 16 byte. Server sinh session_id; chỉ kích hoạt/thay phiên sau gói mã hóa hợp lệ.
- HKDF-SHA256: PSK, salt client_nonce || server_nonce, info c2s/s2c; khóa mỗi chiều riêng. ChaCha20-Poly1305 xác thực toàn bộ header, tag 16 byte.
- Nonce 4 byte 0 || uint64 seq, bắt đầu 0; cấp số dưới khóa, đóng trước tràn. Replay window 4096 cập nhật sau AEAD hợp lệ.
- MaxDatagramSize 1400; MaxPayloadSize = 1400 - 34 - 16 = 1350. 2A giữ nguyên toàn bộ wire format, HKDF và hằng số.

## Bước 2A — Completed: Windows và Ubuntu Linux TUN thật đã đạt

Nghiệm thu Ubuntu dựa trên kết quả thực tế do người dùng xác nhận, không phải Codex trực tiếp chạy. Ngày kiểm thử: 2026-11-21; ngày ghi nhận xác nhận: 2026-11-22. Chi tiết nghiệm thu ở cuối tài liệu; các ghi chú chờ kiểm chứng trước đó được giữ lại theo lịch sử.

### Nội dung

- ITunDevice dùng chung cho Linux và TUN giả; TunForwarder độc lập nền tảng.
- Kiểm tra IPv4 chặt: >=20 byte, version 4, IHL hợp lệ, Total Length khớp số byte, <= Protocol.MaxPayloadSize. Loại IPv6/gói sai/quá lớn.
- Client → TUN chống giả mạo Source IP theo client_id; từ chối session đã cũ/hết hạn. TUN → client tra Destination IP, mã hóa và gửi đúng endpoint phiên.
- Client nhận DATA hai chiều trong thư viện; channel 64 ghi TryWrite không chặn phản hồi điều khiển. SendDataAsync không chờ KEEPALIVE/PROBE; giữ an toàn sequence/replay bằng khóa session và SecureSession.
- Giữ channel server 64 và các assertions Phase 1. Kiểm tra Virtual IP không trùng đã có trong Configuration.cs, giữ nguyên và bổ sung test constructor server.
- LinuxTunDevice: TUN không persistent/độc quyền tun0, 10.77.0.1/24, MTU từ Protocol.MaxPayloadSize, UP; nonblocking + poll trên worker chuyên dụng. Process ip chỉ setup, ArgumentList, timeout/exit code; đóng fd rollback nếu lỗi. Hủy/chờ worker trước dispose.
- Không có Wintun, client app, route Windows, REPL; không thay NAT/firewall hoặc tự triển khai 2B–2E.
- AGENTS.md bắt buộc cập nhật tiến độ sau từng bước test đạt, phân biệt kết quả tự chạy và do người dùng xác nhận.

### File tạo/sửa

| File | Công việc |
|---|---|
| src/DualRoute.Tunnel/ITunDevice.cs | Tạo hợp đồng thiết bị gói IP có cancellation/dispose |
| src/DualRoute.Tunnel/Ipv4Packet.cs | Tạo kiểm tra IPv4 dùng chung |
| src/DualRoute.Tunnel/LinuxTunDevice.cs | Tạo TUN native và setup/cleanup có giới hạn |
| src/DualRoute.Tunnel/TunForwarder.cs | Tạo cầu nối IPv4 hai chiều không phụ thuộc Linux |
| src/DualRoute.Tunnel/TunnelServer.cs | Thêm ánh xạ phiên/gửi DATA và vòng đời RunAsync có TUN |
| src/DualRoute.Tunnel/TunnelClient.cs | Tách DATA/control, gửi không chờ control, cleanup |
| tests/DualRoute.Tunnel.Tests/FakeTunDevice.cs | Tạo thiết bị giả bounded, mô phỏng lỗi/chặn |
| tests/DualRoute.Tunnel.Tests/TunForwarderTests.cs | IPv4, spoof, hai client, phiên cũ/idle, cleanup |
| tests/DualRoute.Tunnel.Tests/TunnelDataTests.cs | DATA/control đồng thời, full queue, replay, oversize, duplicate IP qua tests forwarder |
| tests/DualRoute.Tunnel.Tests/LinuxTunTests.cs | Test Linux bật riêng: kernel ICMP echo, MTU, độc quyền, xóa TUN |
| README.md | Hướng dẫn API, Linux test và giới hạn 2A |
| AGENTS.md | Quy tắc tự động cập nhật PROGRESS.md; cập nhật bẫy DATA |
| PROGRESS.md | Nhật ký lịch sử và xác minh bước 2A |

Không sửa Configuration.cs/Protocol.cs/Transport.cs/SecureSession.cs, không sửa/xóa test cũ hoặc Network Monitor; không thêm NuGet.

### Kết quả tự chạy trên Windows, 2026-11-21

    dotnet build GameNetworkOptimizer.slnx -c Release
    dotnet test GameNetworkOptimizer.slnx -c Release

- Build: PASS, 0 warnings, 0 errors.
- Tests: 60 PASS, 0 FAIL, 1 SKIP (61 ca tổng).
- Network Monitor: 12 pass; Tunnel: 48 pass, 1 skip.
- Toàn bộ 38 tests nền tảng Phase 1/bản vá heartbeat vẫn pass.
- Ca skip: KernelIcmpEchoTraversesTunnelAndTunIsRemovedOnShutdown, cần Linux và biến DUALROUTE_TEST_LINUX_TUN=1.
- Sandbox ban đầu chặn NuGet (NU1301); chạy lại với quyền mạng thành công. Không thêm dependency.

### Chưa kiểm chứng tại lượt triển khai ban đầu (giữ lịch sử)

- Linux TUN native và cleanup trên Ubuntu thật, NAT/Internet, game, Wintun/route, tải dài.
- Bản build đa nền tảng và fake tests pass trên Windows không chứng minh TUN native đã chạy trên Linux.
- Xem README mục “Bước 2A” để chạy lệnh Linux bật riêng. Sau test phải kiểm tra tun0 đã biến mất; không xóa thiết bị có sẵn của người dùng.

## Tiếp theo

| Bước | Trạng thái |
|---|---|
| 2A Linux runtime | Completed — Ubuntu 2026-11-21: người dùng xác nhận 61 PASS, 0 FAIL, 0 SKIP và cleanup tun0; Windows: 60 PASS, 0 FAIL, 1 SKIP |
| 2B Client Wintun | Đã triển khai thư viện và adapter-only recovery; Windows unit/DLL tests đạt, chờ nghiệm thu native bằng Administrator |
| 2C Routing | Chưa triển khai |
| 2D CLI REPL | Chưa triển khai |
| 2E Thử game | Chưa triển khai |
| Phase 3 Cleanup/crash recovery | Chưa triển khai |
| Phase 4 Test Mode/CSV tích hợp | Chưa triển khai |
| Phase 5 VPS Singapore/bằng chứng | Chưa triển khai |

Sau mỗi bước hoàn tất và test pass, cập nhật trạng thái, file, kết quả theo nền tảng và phần chưa kiểm chứng tại đây; không ghi “đạt” cho bước chưa được chạy thực tế.

## Theo dõi kiểm thử Linux TUN — sửa assertion

- Người dùng đã chạy DUALROUTE_TEST_LINUX_TUN=1 trên Ubuntu 192.168.1.200: xác nhận tạo tun0 (Index=3) và dọn sạch khi tắt; test dừng ở assertion tìm interface/MTU, chưa xác nhận ICMP echo hai chiều.
- Rà soát LinuxTunDevice.cs: đã gọi ip address add 10.77.0.1/24 và ip link set dev tun0 mtu Protocol.MaxPayloadSize up; không thay mã runtime.
- Sửa duy nhất test LinuxTunTests.cs: tách tìm đúng một interface theo tên, chấp nhận OperationalStatus.Up hoặc Unknown, kiểm tra MTU riêng và thêm kiểm tra IP 10.77.0.1 với PrefixLength=24.
- Assertion cũ không kiểm tra OperationalStatus, nên chưa kết luận Unknown là nguyên nhân gốc. Assertion mới phân biệt rõ lỗi thiếu interface, trạng thái, MTU hay IP/prefix; vẫn giữ yêu cầu MTU 1350.
- Windows: dotnet build GameNetworkOptimizer.slnx -c Release thành công, 0 warnings/errors; dotnet test GameNetworkOptimizer.slnx -c Release: 60 pass, 0 fail, 1 skip (Linux TUN).
- File thay đổi: tests/DualRoute.Tunnel.Tests/LinuxTunTests.cs và PROGRESS.md.
- Cần chạy lại test Linux bật riêng để xác nhận assertion mới và ICMP echo; chưa đánh dấu 2A Linux hoàn tất.

## Theo dõi kiểm thử Linux TUN — đọc MTU từ sysfs

- Người dùng xác nhận Ubuntu 192.168.1.200 đã qua kiểm tra tun0 và IP 10.77.0.1/24. GetIPv4Properties().Mtu trả 0, khiến assertion MTU kỳ vọng 1350 thất bại.
- LinuxTunTests.cs chuyển sang đọc /sys/class/net/{tun.Name}/mtu bằng File.ReadAllTextAsync với CancellationToken, phân tích số bằng InvariantCulture và so sánh với Protocol.MaxPayloadSize (1350).
- Giữ nguyên logic LinuxTunDevice.cs và các kiểm tra tên interface, trạng thái, IP/prefix, ICMP echo, cleanup.
- Đã chạy trên Windows: dotnet build GameNetworkOptimizer.slnx -c Release đạt 0 warnings/errors; dotnet test GameNetworkOptimizer.slnx -c Release đạt 60 pass, 0 fail, 1 skip (Linux TUN thật).
- File sửa: tests/DualRoute.Tunnel.Tests/LinuxTunTests.cs, PROGRESS.md.
- Chưa chạy lại assertion sysfs và ICMP echo trên Ubuntu trong lượt sửa này. Lệnh xác minh: sudo env DUALROUTE_TEST_LINUX_TUN=1 dotnet test tests/DualRoute.Tunnel.Tests -c Release --filter Category=LinuxTun.

## Nghiệm thu Phase 2A — người dùng xác nhận Ubuntu

- Ngày chạy kiểm thử: 2026-11-21 (hiểu ngày người dùng nhập “21 /11/220226” là 21/11/2026); ngày người dùng xác nhận trong cuộc trao đổi và cập nhật nhật ký: 2026-11-22.
- Nền tảng: Ubuntu, máy LAN 192.168.1.200, .NET 10.
- Thông tin lệnh người dùng cung cấp: DUALROUTE_TEST_LINUX_TUN=1. Đây là biến môi trường bật ca Linux TUN thật; người dùng chưa cung cấp nguyên dòng lệnh dotnet đã chạy.
- Kết quả thực tế do người dùng xác nhận: 61/61 PASS, 0 FAIL, 0 SKIP; tun0 được cleanup thành công sau khi ứng dụng kết thúc.
- Codex không trực tiếp chạy bộ kiểm thử Ubuntu này. Kết quả xác nhận hoàn tất kiểm thử Linux TUN thật của 2A, bao gồm ca kernel ICMP echo và cleanup; khép lại các mục chờ kiểm chứng Linux ở những lượt trước.
- Trạng thái Phase 2A: Completed. Chưa suy ra NAT/Internet, VPS, game, tải dài hoặc crash recovery đã được kiểm chứng.

### Kiểm tra lại do Codex chạy trên Windows — 2026-11-22

    dotnet build GameNetworkOptimizer.slnx -c Release
    dotnet test GameNetworkOptimizer.slnx -c Release

- Build: PASS, 0 warnings, 0 errors.
- Tests: 60 PASS, 0 FAIL, 1 SKIP (61 ca); Network Monitor: 12 PASS; Tunnel: 48 PASS, 1 SKIP.
- Ca skip là KernelIcmpEchoTraversesTunnelAndTunIsRemovedOnShutdown, yêu cầu Linux TUN thật. Kết quả này độc lập với xác nhận Ubuntu của người dùng.
- Lần build đầu trong sandbox bị chặn truy cập NuGet (NU1301); chạy lại với quyền mạng đạt. Không thêm package hoặc đổi dependency.
- Lệnh kiểm tra thủ công Ubuntu để tái xác minh, không phải nguyên dòng lệnh người dùng đã cung cấp:

      ip link show tun0
      sudo env DUALROUTE_TEST_LINUX_TUN=1 dotnet test GameNetworkOptimizer.slnx -c Release
      ip link show tun0

  Trước và sau test phải không có tun0; nếu có thiết bị sẵn thì dừng, không xóa tùy tiện. Lệnh tái xác minh này chưa được Codex chạy trên Ubuntu.
- Đợt cập nhật nghiệm thu chỉ sửa PROGRESS.md, giữ lịch sử trước đó; không sửa source/tests, không tạo file triển khai Wintun, không commit/push. Phase 2B chỉ nghiên cứu và lập kế hoạch, chưa triển khai; không chuyển sang 2C.

## Phase 2B — triển khai Windows Wintun, 2026-11-22

Người dùng duyệt kế hoạch và ngoại lệ WintunRecovery: chỉ journal/recovery adapter
DualRoute qua wintun-state.json, không state.json tổng thể, route cleanup hay Phase 3.
Mục cập nhật nghiệm thu 2A phía trên là lịch sử lượt trước, không mô tả phạm vi lượt này.
Ubuntu 2A vẫn Completed theo xác nhận của người dùng, không phải Codex chạy Ubuntu.

### File tạo mới

- src/DualRoute.Client.Windows/DualRoute.Client.Windows.csproj: thư viện net10.0-windows, tham chiếu core, không NuGet runtime mới.
- WintunDevice.cs trong project trên: ITunDevice, receive/send worker chuyên dụng, channels 64, validation IPv4, drop counters, cancellation và disposal idempotent.
- WintunNative.cs: chữ ký từ header 0.14.1, absolute DLL path, PE AMD64/hash, module lifetime và borrowed read event.
- WindowsAdapterConfiguration.cs: IP Helper cấu hình IP /24 từ HELLO_ACK, MTU Protocol.MaxPayloadSize, administrative UP; SetupAPI xác minh/gỡ đúng instance.
- WintunRecovery.cs: schema v1, GUID cố định, tên có operation ID, identity thực tế, ACL Administrators/SYSTEM, lease và atomic journal; recovery chỉ adapter.
- ClientTunForwarder.cs: cầu nối client hai chiều, một reader ReceivedData, kiểm source/destination và dừng khi client thất bại/IP đổi.
- Native/wintun.dll, Native/wintun.h, Native/LICENSE.txt trong project Windows: lấy từ archive chính thức 0.14.1 đã kiểm hash.
- tests/DualRoute.Client.Windows.Tests/DualRoute.Client.Windows.Tests.csproj, WintunDeviceTests.cs, ClientTunForwarderTests.cs, WintunIntegrationTests.cs: native/system giả, DLL thật không tạo adapter, hai integration bật riêng. Tái dùng TestTransport.cs/FakeTunDevice.cs hiện có qua source link; không sửa các file đó. Project test Windows không chạy trên Linux.
- tests/DualRoute.Wintun.TestHost/DualRoute.Wintun.TestHost.csproj, Program.cs: fixture process cho integration kill/restart, không CLI sản phẩm.

### File chỉnh sửa

- AGENTS.md: ngoại lệ journal adapter 2B, native DLL/buffer ownership, cleanup và giới hạn phạm vi; giữ quy tắc khác.
- GameNetworkOptimizer.slnx: thêm Windows library, test project và integration test host.
- README.md: trạng thái 2A/2B, tích hợp, nguồn/hash DLL, vòng đời, journal/recovery, lệnh kiểm thử và giới hạn.
- PROGRESS.md: ghi kết quả thực tế và trạng thái chờ nghiệm thu native.
- Không sửa source hoặc tests Phase 1/2A, không đổi protocol/MTU, không đụng source GameNetworkOptimizer. Không commit/push.

### Binary và thiết kế đã triển khai

- URL: https://www.wintun.net/builds/wintun-0.14.1.zip; ZIP SHA-256 đã kiểm trước giải nén: 07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51.
- Entry DLL: wintun/bin/amd64/wintun.dll; DLL SHA-256: E5DA8447DC2C320EDC0FC52FA01885C103DE8C118481F683643CACC3220DAFCE.
- DLL/license copy cạnh host executable; runtime không tải mạng, không tìm theo CWD/PATH. Header và SDK netioapi.h chính thức đã đối chiếu trước khai báo native.
- Client IP lấy từ TunnelClient.VirtualIp sau Connected; loại 10.77.0.1/server, .0/.255, ngoài subnet và IPv6. MTU chỉ lấy Protocol.MaxPayloadSize (1350).
- Stop/dispose: dừng/join worker → EndSession → CloseAdapter handle Create → xác minh removal → xóa journal. Không nhầm CloseAdapter handle Open với xóa thiết bị.
- Journal tại %ProgramData%/DualRoute/Wintun/wintun-state.json: Version, Owner, AdapterGuid, Name, OperationId, Stage, InstanceId. Ghi file tạm WriteThrough/Flush(true) trước rename. Không chứa PSK/payload.
- Recovery đọc journal trước khi tạo adapter, xác minh type/hardware/GUID/tên/instance; state hỏng, ownership mơ hồ, ACL không tin cậy hoặc identity xung đột thì không xóa. Gỡ đúng instance bằng SetupAPI, không xóa driver dùng chung. Lỗi/reboot-required giữ journal. Không cam kết mọi tình huống mất điện/storage.

### Codex tự chạy trên Windows AMD64, .NET SDK 10.0.401

    dotnet build GameNetworkOptimizer.slnx -c Release
    dotnet test GameNetworkOptimizer.slnx -c Release

- Build cuối: PASS, 0 warnings, 0 errors.
- Test cuối toàn solution: 84 PASS, 0 FAIL, 3 SKIP, tổng 87 ca.
- Network Monitor: 12 PASS. Tunnel nền tảng: 48 PASS, 1 SKIP (Linux TUN thật).
- Windows 2B: 24 PASS, 0 FAIL, 2 SKIP. Bao gồm DLL thật load/resolve export/unload, không tạo adapter; unit tests native/system giả, send/receive/buffer ownership, failure rollback, cancellation/dispose, invalid IP/DLL, recovery và fake client forwarding.
- Đã bật DUALROUTE_TEST_WINTUN=1 và chạy --filter Category=Wintun: 0 PASS, 0 FAIL, 2 SKIP do token hiện tại không có Administrator. Không tạo adapter thật; đây KHÔNG phải integration pass.
- Trong quá trình phát triển có lỗi compile nullable và một assertion mới kỳ vọng sai khi client dispose; đã sửa và chạy lại toàn solution đạt kết quả trên. Không thay tests cũ để làm pass.
- git diff --check đạt; cảnh báo Git LF/CRLF không phải lỗi whitespace.

### Kiểm thử thủ công và phần chưa kiểm chứng

PowerShell x64 Run as Administrator, trên máy/VM thử nghiệm, không có client DualRoute đang chạy:

    $env:DUALROUTE_TEST_WINTUN = '1'
    try {
        dotnet test tests/DualRoute.Client.Windows.Tests -c Release --filter Category=Wintun
    } finally { Remove-Item Env:DUALROUTE_TEST_WINTUN }

- Hai ca thật: IP/MTU/UP + kernel IPv4 ICMP echo + normal cleanup; kill test host → startup recovery hai lần → adapter khác nguyên vẹn. Test host chỉ chạy trong integration, ArgumentList không shell, có deadline/cleanup process trong finally.
- Chưa chạy các ca trên bằng Administrator; native IP Helper/SetupAPI và ACL tạo journal elevated chưa được nghiệm thu thực tế. DLL loader thật đã kiểm, không đồng nghĩa driver/adapter thật đạt.
- Chưa chạy reboot, Ctrl+C/đóng cửa sổ trên host thật, tải dài, NAT/Internet hoặc game. README có quy trình kiểm tra adapter/IP/MTU và reboot trên VM. Không thay đổi route/default gateway/host route hoặc triển khai CLI cleanup.
- Trạng thái 2B: code và unit tests đã đạt; CHƯA Completed theo Definition of Done vì native integration/cleanup thật còn chờ người dùng chạy. Dừng tại 2B để review, không chuyển 2C hoặc Phase 3.

## Phase 2B — sửa cách kiểm tra IPv4 MTU, ghi nhận 2026-11-22

- Người dùng báo đã chạy Category=Wintun bằng PowerShell Administrator: 1 PASS (kill/recovery), 1 FAIL tại assertion MTU của RealAdapterHasIpMtuUpAndKernelEchoThenDisappears: Expected 1350, Actual 65535. Đây là kết quả người dùng xác nhận, Codex không trực tiếp chạy lượt Administrator này; chưa có ngày chạy riêng được cung cấp.
- Đối chiếu netioapi.h, nldef.h và ws2def.h của Windows SDK: MIB_IPINTERFACE_ROW x64 có alignment 8, size 168; layout đang dùng đúng. BOOLEAN và NL_INTERFACE_OFFLOAD_ROD mỗi loại chiếm 1 byte; ZoneIndices có 16 ULONG. Không đổi offset MTU hoặc giá trị Protocol.MaxPayloadSize.
- Đã kiểm tra độc lập layout tuần tự đủ các field theo SDK bằng Marshal trên process x64: Family=0, InterfaceLuid=8, InterfaceIndex=16, MaxReassemblySize=20, InterfaceIdentifier=24, MinRouterAdvertisementInterval=32, MaxRouterAdvertisementInterval=36; AdvertisingEnabled/ForwardingEnabled/WeakHostSend/WeakHostReceive/UseAutomaticMetric/UseNeighborUnreachabilityDetection/ManagedAddressConfigurationSupported/OtherStatefulConfigurationSupported/AdvertiseDefaultRoute lần lượt 40–48; RouterDiscoveryBehavior=52, DadTransmits=56, BaseReachableTime=60, RetransmitTime=64, PathMtuDiscoveryTimeout=68, LinkLocalAddressBehavior=72, LinkLocalAddressTimeout=76; ZoneIndices[i]=80+4*i (i=0..15); SitePrefixLength=144, Metric=148, NlMtu=152; Connected/SupportsWakeUpPatterns/SupportsNeighborDiscovery/SupportsRouterDiscovery lần lượt 156–159; ReachableTime=160, TransmitOffload=164, ReceiveOffload=165, DisableDefaultRoutes=166; padding cuối tới size 168.
- Nguồn .NET 10 SystemIPv4InterfaceProperties cho thấy GetIPv4Properties().Mtu lấy IP_ADAPTER_ADDRESSES.mtu, không truy vấn NlMtu của IPv4 subinterface. Vì vậy assertion cũ có thể nhận link MTU Wintun 65535 dù IPv4 MTU đã cấu hình 1350; lỗi đọc assertion không chứng minh SetIpInterfaceEntry thất bại.
- Diff sửa: WindowsAdapterConfiguration.cs thêm InterfaceIndex ở offset 16 và GetIpv4Mtu(index) tạo row mới Family=AF_INET, Luid=0 rồi GetIpInterfaceEntry; ConfigureAsync đọc lại qua helper này, tránh tái dùng buffer đã chứa giá trị vừa set. Giữ Get → chỉ sửa Mtu/SitePrefixLength → Set như cũ. WintunIntegrationTests.cs kiểm MTU bằng helper với IPv4 interface index của đúng adapter, không dùng .NET Mtu.
- Thêm WindowsAdapterConfigurationTests.cs: kiểm ABI các field được khai báo, truy vấn IPv4 loopback thật không cần Administrator và xác minh index không hợp lệ báo lỗi. File tài liệu sửa trong lượt này: PROGRESS.md; không đổi dependency/giao thức/route, không commit/push.
- Codex đã chạy dotnet build GameNetworkOptimizer.slnx -c Release: PASS, 0 warnings, 0 errors.
- Codex đã chạy dotnet test GameNetworkOptimizer.slnx -c Release: 86 PASS, 0 FAIL, 3 SKIP, tổng 89. Windows 2B: 26 PASS, 2 SKIP; nền tảng tunnel: 48 PASS, 1 SKIP; Network Monitor: 12 PASS. Hai regression tests mới đã PASS trên Windows.
- Chưa tự chạy lại adapter thật bằng Administrator trong lượt sửa này; hai ca Wintun và Linux TUN thật vẫn SKIP trong kết quả Codex. Recovery native đã PASS theo người dùng; IPv4 MTU/ICMP/cleanup của ca còn lại cần chạy lại bằng Administrator. 2B vẫn chờ nghiệm thu, không chuyển 2C.
- Lệnh chạy lại cho người dùng (PowerShell Administrator):

      $env:DUALROUTE_TEST_WINTUN = '1'
      try {
          dotnet test tests/DualRoute.Client.Windows.Tests -c Release --filter Category=Wintun
      } finally { Remove-Item Env:DUALROUTE_TEST_WINTUN }

- Nguồn đối chiếu: [Windows SDK netioapi.h](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/shared/netioapi.h), [GetIpInterfaceEntry](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-getipinterfaceentry), [.NET 10 SystemIPv4InterfaceProperties](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Net.NetworkInformation/src/System/Net/NetworkInformation/SystemIPv4InterfaceProperties.cs).

## Phase 2B — bỏ phụ thuộc inbound ICMP trong integration, ghi nhận 2026-11-22

- Người dùng xác nhận assertion IPv4 MTU 1350 đã PASS sau bản sửa trước; ca RealAdapterHasIpMtuUpAndKernelEchoThenDisappears tiếp tục timeout sau khoảng 45 giây tại Channel.ReadAsync. Đây là kết quả Administrator do người dùng báo, Codex không trực tiếp chạy lượt đó; recovery PASS đã được ghi nhận ở mục trước.
- Kiểm tra test cũ: bơm IPv4 ICMP Echo Request 10.77.0.1 → 10.77.0.2 vào Windows stack và chờ kernel Echo Reply. Gói 28 byte có checksum IPv4 0x6645, ICMP 0xf7fd theo kiểm tra độc lập; không thấy lỗi checksum. Inbound ICMP trên profile Public có thể bị firewall drop im lặng; chưa có capture/drop log để khẳng định nguyên nhân duy nhất của timeout.
- Đối chiếu worker WintunDevice.cs với header 0.14.1: Receive đến ERROR_NO_MORE_ITEMS (259) rồi WaitAny trên read event/work/stop; read event được mượn, không reset/CloseHandle; receive buffer luôn release trong finally. Không thấy lỗi mất wakeup từ rà soát này, giữ nguyên mã runtime WintunDevice/WintunNative.
- Sửa WintunIntegrationTests.cs: đổi tên ca thành RealAdapterHasIpMtuUpAndUdpRoundTripThenDisappears. UdpClient bind IP client 10.77.0.2 và port hệ điều hành cấp, connect 10.77.0.1:39001 rồi gửi payload ngẫu nhiên. Đọc gói outbound qua Wintun, đối chiếu IPv4/protocol/IP/ports/UDP length/payload; đảo IP và port để bơm phản hồi về đúng socket. Recompute IPv4 checksum, đặt UDP checksum 0 hợp lệ với IPv4 theo RFC 768, không tin checksum TX có thể offload. Xác nhận socket nhận đúng endpoint/payload. Giữ MTU/IP/UP và cleanup assertions.
- Timeout giờ báo rõ đang ở bước outbound capture hay UDP response delivery, kèm DroppedPackets. Test dùng connected subnet do gán IP tạo ra, không thêm route hoặc sửa firewall/profile. Outbound policy tùy chỉnh vẫn có thể chặn UDP.
- WintunDeviceTests.cs thêm regression ReadEventWakesWorkerAfterReceiveRingWasEmpty: chờ fake Receive báo ring rỗng, tạo pending read, bơm packet và signal read event, xác minh payload rồi dispose/join trước kiểm release buffer. Test này xác minh worker bằng native giả, không thay thế test ring thật.
- README.md cập nhật mô tả UDP roundtrip; PROGRESS.md ghi lịch sử. Chỉ sửa hai file test và hai file tài liệu trong lượt này; không thay source runtime, protocol/MTU/dependency, không commit/push.
- Codex tự chạy Windows: dotnet build GameNetworkOptimizer.slnx -c Release PASS, 0 warnings, 0 errors; dotnet test GameNetworkOptimizer.slnx -c Release: 87 PASS, 0 FAIL, 3 SKIP (90 ca). Windows 2B 27 PASS/2 SKIP, core 48 PASS/1 SKIP, Network Monitor 12 PASS. Regression read-event mới PASS.
- Chưa tự chạy integration thật bằng Administrator: 2 Wintun tests và 1 Linux TUN test vẫn SKIP trong kết quả trên. Cần người dùng chạy lại Category=Wintun bằng lệnh Administrator ở mục trước; lệnh filter không thay đổi sau rename. 2B vẫn chờ nghiệm thu UDP/cleanup native, không chuyển 2C.
- Nguồn: header Native/wintun.h đã đóng gói; [Windows Firewall inbound ICMP/outbound policy](https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/windows-firewall/configure), [RFC 768](https://www.rfc-editor.org/rfc/rfc768).

## Phase 2B — chuẩn hóa assertion cuối cleanup, ghi nhận 2026-11-22

- Người dùng xác nhận UDP hai chiều qua Wintun thật đã PASS; RealAdapterHasIpMtuUpAndUdpRoundTripThenDisappears chạy tới dòng 87 rồi fail Assert.True. Đây là kết quả Administrator do người dùng báo, không phải Codex trực tiếp chạy.
- Dòng 87 cũ kiểm before.IsSubsetOf(NetworkInterface hiện tại), tức mọi adapter có trước test vẫn còn, không phải kiểm DN_STARTED/present/uninstalled hoặc journal. Assertion SetupAPI không còn GUID DualRoute ở dòng 86 đã đi qua; DisposeAsync cũng đã trả về sau CompleteAsync. Do đó chưa có bằng chứng lỗi native cleanup từ failure này.
- Rà soát runtime: WintunDevice dừng/join worker → EndSession → CloseAdapter → WintunRecovery.CompleteAsync. CompleteAsync đã poll system.Find tối đa 10 giây bằng Task.Delay(50), rồi mới xóa journal. Không sửa runtime hoặc nới điều kiện recovery trong lượt này.
- Sửa riêng WintunIntegrationTests.cs: baseline bảo toàn adapter chỉ loại GUID cố định của DualRoute (instance cũ có thể được recovery khi tạo thiết bị); adapter khác vẫn bắt buộc còn nguyên. So sánh ID không phân biệt hoa/thường. Sau Dispose, giữ assertion SetupAPI, thêm assertion journal không còn, poll NetworkInterface mỗi 50 ms tối đa 5 giây để chờ PnP/enumeration ổn định. Cuối cùng xác minh GUID DualRoute biến mất và không thiếu adapter khác; failure liệt kê ID bị thiếu hoặc GUID DualRoute còn hiện diện. Không xem trạng thái chưa started là đã uninstall.
- Chưa có danh sách ID bị thiếu từ assertion cũ, nên chưa khẳng định do instance DualRoute cũ hay thay đổi adapter/enumeration của Windows. Polling không bỏ qua adapter khác bị mất; nếu lỗi còn xảy ra, thông báo mới cung cấp danh sách để điều tra.
- Chỉ sửa test trên và PROGRESS.md trong lượt này, không thêm package, không sửa route/firewall, không commit/push.
- Codex đã chạy dotnet build GameNetworkOptimizer.slnx -c Release: PASS, 0 warnings, 0 errors. dotnet test GameNetworkOptimizer.slnx -c Release: 87 PASS, 0 FAIL, 3 SKIP (90 ca): Windows 2B 27 PASS/2 SKIP, core 48 PASS/1 SKIP, Network Monitor 12 PASS. git diff --check đạt.
- Chưa chạy lại Category=Wintun bằng Administrator trong lượt sửa; cần người dùng chạy lại hai integration tests bằng lệnh ở mục trước. UDP thật và recovery thật đã được người dùng xác nhận, nhưng toàn bộ 2B vẫn chờ assertion cuối pass, chưa đánh dấu Completed hoặc chuyển 2C.

## Phase 2B — race cleanup fixture giữa hai integration tests, 2026-11-22

- Người dùng gửi log Administrator: UDP test fail ở dòng 104 vì baseline thiếu GUID {C05FA9DA-352C-47DD-843A-E3C518024616} sau 5 giây. Các assertion UDP roundtrip, GUID DualRoute biến mất và journal đã xóa đều đã đi qua. Đây là kết quả người dùng chạy, không phải Codex.
- Codex đọc C:/Windows/INF/setupapi.dev.log trên máy hiện tại: GUID thiếu thuộc SWD/WINTUN, có Delete Device lúc 2026/11/22 10:18:21.013–10:18:21.054; lần Delete Device GUID DualRoute kế tiếp lúc 10:18:24.833. Adapter đó hiện không còn trong NetworkInterface và registry adapter. Không có bằng chứng adapter vật lý bị mất; cũng không khẳng định được tên lịch sử của GUID chỉ từ log này.
- Rà soát thấy race trong KilledProcessIsRecoveredWithoutTouchingForeignAdapter: adapter đối chứng OtherTest có GUID ngẫu nhiên được CloseAdapter trong finally, nhưng test không chờ PnP/NetworkInterface removal trước khi trả về. Test UDP kế tiếp có thể snapshot fixture đang gỡ rồi báo mất adapter. Thời điểm trong log phù hợp với race này; cần chạy lại native để xác nhận hết lỗi.
- Sửa WintunIntegrationTests.cs: lưu foreignGuid của fixture; sau CloseAdapter chờ tối đa 10 giây bằng Task.Delay(50) tới khi GUID đó vắng cả system.Find và NetworkInterface. Giữ DLL sống trong lúc chờ; nếu timeout báo đúng GUID. Cleanup journal DualRoute vẫn chạy trong finally kể cả fixture cleanup lỗi.
- Không xóa/ẩn adapter lạ hoặc nới assertion bảo toàn baseline. Baseline giờ giữ cả tên, mô tả, trạng thái để thông báo rõ adapter nào thiếu nếu lỗi khác còn xảy ra. Giữ nguyên runtime WintunDevice/WintunRecovery, route/firewall và protocol.
- Chỉ sửa file integration test và PROGRESS.md trong lượt này. Codex chạy dotnet build GameNetworkOptimizer.slnx -c Release: PASS, 0 warnings, 0 errors; dotnet test GameNetworkOptimizer.slnx -c Release: 87 PASS, 0 FAIL, 3 SKIP (90 ca). Windows 2B 27 PASS/2 SKIP, core 48 PASS/1 SKIP, Network Monitor 12 PASS.
- Hai integration Wintun vẫn SKIP trong lượt Codex vì không bật/quyền Administrator; chưa xác nhận native sau bản sửa này. Chạy lại Category=Wintun bằng lệnh Administrator đã ghi phía trên; 2B vẫn chờ nghiệm thu, không commit/push hoặc chuyển 2C.
