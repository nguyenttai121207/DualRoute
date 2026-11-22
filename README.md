# DualRoute — Chẩn đoán mạng, tunnel Phase 1 và Server TUN 2A

DualRoute là dự án xây dựng nền tảng chẩn đoán mạng và thử nghiệm đường truyền cho trò chơi trực tuyến trên Windows 11. Phiên bản hiện tại gồm một ứng dụng dòng lệnh đo ICMP và một thư viện giao thức UDP có mã hóa, xác thực, quản lý phiên và nhiều máy khách.

**Trạng thái:** Phase 1 và 2A đã đạt; người dùng xác nhận Ubuntu LAN 61/61 tests và cleanup tun0 ngày 2026-11-21. Thư viện Windows Wintun 2B đã triển khai, đang chờ nghiệm thu adapter thật bằng Administrator. Xem [PROGRESS.md](PROGRESS.md) để biết nền tảng đã kiểm chứng. Chưa có ứng dụng tunnel server/client độc lập hoặc chuyển tiếp game/Internet; VPS chưa kiểm chứng.

## 1. Mục tiêu và phạm vi

- Cung cấp số liệu ICMP phục vụ chẩn đoán độ trễ, dao động độ trễ, mất phản hồi và các đợt tăng độ trễ.
- Xây dựng giao thức tunnel UDP v1 với khóa chia sẻ trước, mã hóa hai chiều và chống phát lại gói tin.
- Quản lý độc lập nhiều client trên cùng server, chuẩn bị nền tảng cho các bước phát triển sau.
- Giữ thư viện tunnel độc lập với Network Monitor và giao diện.

Hiện chưa có VPN hoàn chỉnh, chuyển tuyến tự động, Multipath, FEC, tích hợp sing-box/UDPspeeder hay WinUI 3. Ứng dụng không sửa bảng định tuyến, không can thiệp Valorant, Riot Vanguard hoặc bộ nhớ trò chơi.

## 2. Kiến trúc tổng thể

~~~mermaid
flowchart LR
    subgraph Monitor["Chẩn đoán ICMP — GameNetworkOptimizer"]
        CLI["Program và ConsoleDashboard"]
        NM["NetworkMonitor"]
        AN["Các bộ phân tích số liệu"]
        CSV["CsvLogger"]
        CLI --> NM
        NM --> CLI
        CLI --> AN
        CLI --> CSV
    end
    subgraph Tunnel["Thư viện DualRoute.Tunnel — độc lập với ICMP"]
        TC["TunnelClient"]
        TR["IDatagramTransport / UDP"]
        TS["TunnelServer"]
        SC["SecureSession và ReplayWindow"]
        CFG["ServerConfiguration"]
        TC <--> TR
        TR <--> TS
        TC --> SC
        TS --> SC
        CFG --> TS
        TS --> DATA["ReceivedData — hàng đợi dữ liệu"]
    end
~~~

NetworkMonitor thực hiện phép đo; các lớp trong Analyzers tính số liệu. ConsoleDashboard hiện giữ thống kê riêng cho từng đích và trình bày kết quả; Program điều phối ghi CSV.

Trong thư viện tunnel, Protocol và ClientHandshake xử lý định dạng/bắt tay; SecureSession xử lý mã hóa và chống phát lại; TunnelServer quản lý phiên; TunnelClient điều phối kết nối, gói duy trì và phép đo RTT. IDatagramTransport tách giao thức khỏi truyền tải UDP thật hoặc truyền tải giả lập trong kiểm thử.

## 3. Công nghệ sử dụng

| Thành phần | Công nghệ |
|---|---|
| Ngôn ngữ, nền tảng | C#, .NET 10, net10.0 |
| Đo ICMP | System.Net.NetworkInformation.Ping |
| Truyền tải UDP | System.Net.Sockets.Socket |
| Mã hóa có xác thực | ChaCha20Poly1305 |
| Dẫn xuất khóa, xác thực bắt tay | HKDF với SHA-256, HMACSHA256 |
| Thời gian | TimeProvider cho thời hạn tunnel; Stopwatch cho RTT |
| Hàng đợi có giới hạn | System.Threading.Channels |
| Cấu hình và nhật ký | System.Text.Json, CSV UTF-8 |
| Kiểm thử | xUnit 2.9.3, Microsoft.NET.Test.Sdk 17.14.1, xunit.runner.visualstudio 3.1.4 |

Hai dự án ứng dụng/thư viện không dùng gói NuGet bên ngoài. Các gói NuGet nêu trên chỉ phục vụ kiểm thử.

## 4. Cấu trúc thư mục

~~~text
DualRoute/
├── GameNetworkOptimizer.slnx
├── README.md
├── LICENSE
├── config.example.json
├── src/
│   ├── GameNetworkOptimizer/
│   │   ├── Analyzers/
│   │   ├── Models/
│   │   ├── Presentation/
│   │   ├── Services/
│   │   └── Program.cs
│   └── DualRoute.Tunnel/
│       ├── ClientHandshake.cs
│       ├── Configuration.cs
│       ├── Protocol.cs
│       ├── SecureSession.cs
│       ├── Transport.cs
│       ├── TunnelClient.cs
│       └── TunnelServer.cs
├── tests/
│   ├── GameNetworkOptimizer.Tests/
│   └── DualRoute.Tunnel.Tests/
└── data/                         # Được tạo khi ghi nhật ký ICMP
~~~

Tên project GameNetworkOptimizer được giữ nguyên trong mã và lệnh chạy.

## 5. Nguyên lý giao thức tunnel

UDP truyền từng datagram, tức một thông điệp độc lập; bản thân UDP không xác nhận giao hàng. Tunnel bổ sung xác thực, mã hóa, số thứ tự và kiểm tra gói phát lại, nhưng không bổ sung cơ chế truyền lại DATA hay đảm bảo giao hàng.

Các loại gói v1: HELLO=1, HELLO_ACK=2, DATA=3, PROBE=4, PROBE_REPLY=5, KEEPALIVE=6, DISCONNECT=7.

Các trường số nguyên dùng thứ tự byte big-endian, nghĩa là byte có trọng số lớn đứng trước. UUID của client chiếm 16 byte theo thứ tự mạng, khác dạng mảng byte hỗn hợp mặc định cũ của .NET.

| Gói | Bố cục theo thứ tự, kích thước tính bằng byte |
|---|---|
| HELLO — 96 byte | version[1], type[1], client_id[16], client_nonce[16], padding[30], HMAC[32] |
| HELLO_ACK — 96 byte | version[1], type[1], client_id[16], client_nonce[16], session_id[8], server_nonce[16], virtual_ipv4[4], max_datagram_size[2], HMAC[32] |
| Gói mã hóa | header[34], nội dung mã hóa, thẻ xác thực[16] |
| Header gói mã hóa | version[1], type[1], client_id[16], session_id[8], seq[8] |

Protocol.Version bằng 1; MaxDatagramSize bằng 1400 byte; MaxPayloadSize bằng 1350 byte. Gói quá lớn, quá ngắn, sai phiên bản hoặc sai loại bị từ chối. HELLO được đệm bằng byte 0 đến kích thước HELLO_ACK để phản hồi bắt tay không lớn hơn yêu cầu.

DATA hiện đi từ client đến server qua SendDataAsync; ứng dụng tích hợp đọc TunnelServer.ReceivedData. Ở đường chạy Phase 1 không có TUN, server chỉ công bố DATA thô. Từ 2A, overload RunAsync có ITunDevice chuyển tiếp IPv4 hợp lệ vào TUN và mã hóa gói trả về đúng client; TunnelClient.ReceivedData công bố DATA nhận được. Không có ứng dụng client Wintun hoặc chương trình host tunnel độc lập.

## 6. Bắt tay và mã hóa

Client là máy khách, server là máy chủ; session là phiên kết nối. PSK là khóa bí mật chia sẻ trước giữa một client và server. Mỗi client cần PSK riêng có ít nhất 32 byte sau giải mã Base64.

~~~mermaid
sequenceDiagram
    participant C as TunnelClient
    participant S as TunnelServer
    C->>S: HELLO — client_id, client_nonce, HMAC
    Note over S: Kiểm tra HMAC, tạo phiên chờ có thời hạn
    S->>C: HELLO_ACK — session_id, server_nonce, IP ảo, kích thước, HMAC
    Note over C,S: Dẫn xuất khóa c2s và s2c
    C->>S: KEEPALIVE mã hóa đầu tiên
    Note over S: Xác thực thành công rồi mới kích hoạt/thay phiên
    S->>C: KEEPALIVE mã hóa phản hồi
    Note over C: Chuyển sang Connected
~~~

1. Client tạo client_nonce ngẫu nhiên mật mã 16 byte và gửi HELLO. Khóa HMAC được dẫn xuất bằng HKDF-SHA256 từ PSK, salt rỗng, info ASCII là "dualroute v1 handshake", độ dài khóa 32 byte. HMAC xác thực mọi byte đứng trước nó, kể cả phần đệm.
2. Server chỉ trả lời client_id đã cấu hình với HMAC hợp lệ. Server tạo session_id ngẫu nhiên 8 byte khác 0 và server_nonce ngẫu nhiên 16 byte. HMAC của HELLO_ACK bao phủ toàn bộ trường phía trước, bao gồm client_nonce.
3. Hai bên dẫn xuất khóa 32 byte cho mỗi chiều: HKDF-SHA256 với PSK làm đầu vào, salt (dữ liệu bổ sung cho dẫn xuất khóa) là client_nonce nối server_nonce, info (nhãn ngữ cảnh) ASCII là "c2s" hoặc "s2c".
4. Server chưa kích hoạt phiên khi gửi HELLO_ACK. Phiên chỉ được kích hoạt sau gói mã hóa hợp lệ đầu tiên; cách kết nối của TunnelClient dùng KEEPALIVE để xác nhận bước này.

ChaCha20Poly1305 vừa mã hóa nội dung vừa kiểm tra tính toàn vẹn. Toàn bộ header 34 byte là dữ liệu liên kết được xác thực (AEAD associated data): header không bị mã hóa nhưng không thể sửa mà vẫn giữ xác thực hợp lệ.

Nonce là giá trị dùng một lần với mỗi khóa để mã hóa gói. Nonce mã hóa dài 12 byte: bốn byte 0 nối số thứ tự uint64 big-endian. Mỗi chiều có khóa riêng, số thứ tự bắt đầu từ 0 và tăng cho từng lần mã hóa gói gửi. SecureSession dùng khóa đồng bộ để cấp số thứ tự an toàn giữa các luồng và đóng ngữ cảnh mã hóa trước khi số thứ tự tràn.

ReplayWindow dùng cửa sổ trượt 4096 số thứ tự để từ chối gói trùng hoặc quá cũ, đồng thời chấp nhận gói đến sai thứ tự trong cửa sổ. Chỉ gói vượt qua xác thực AEAD mới cập nhật cửa sổ.

## 7. Phiên kết nối và nhiều client

Mỗi phiên có client_id, session_id, địa chỉ IP/cổng từ xa, khóa mã hóa và trạng thái chống phát lại riêng. Phiên được gắn với địa chỉ IP/cổng đã bắt tay; khi NAT đổi ánh xạ cần kết nối lại.

Phiên mới cùng client_id chỉ thay thế phiên đang hoạt động sau khi gói mã hóa mới xác thực thành công. Khi kích hoạt, server giải phóng phiên cũ và các phiên chờ khác của client đó. DISCONNECT có xác thực chỉ xóa phiên tương ứng; không xóa phiên client khác.

| Thiết lập TunnelOptions | Mặc định |
|---|---|
| MaxSessions | 128 phiên hoạt động |
| MaxPendingHandshakes | 128 phiên chờ |
| HandshakeTimeout | 5 giây |
| KeepaliveInterval | 5 giây |
| ResponseTimeout | 10 giây |
| IdleTimeout | 30 giây |

Server dọn phiên hết hạn trước mỗi lần xử lý gói và mỗi giây trong RunAsync. TimeProvider giúp kiểm thử thời hạn mà không phụ thuộc đồng hồ thực.

Client có các trạng thái Connecting, Connected, Reconnecting, Failed. Khi mất phản hồi KEEPALIVE, client thử bắt tay mới một lần; nếu lần thử thất bại thì chuyển Failed. Không có vòng lặp thử lại vô hạn. Bắt tay ban đầu bị mất gói sẽ hết thời hạn; có thể gọi ConnectAsync để yêu cầu kết nối mới.

PROBE chứa mã phép đo 8 byte và dấu thời gian Stopwatch cục bộ 8 byte, đều được ghi big-endian theo Int64. Server trả nguyên 16 byte bằng PROBE_REPLY. Client đối chiếu nội dung và dùng Stopwatch để tính RTT — thời gian khứ hồi — không dùng đồng hồ server.

Giới hạn bộ nhớ gồm số phiên có trần, cửa sổ chống phát lại 4096 mục, hàng đợi nhận client 64 mục và hàng đợi DATA server 64 mục. Hàng đợi đầy có thể làm mất thông điệp; DATA bị bỏ vì đầy được tính vào DroppedPackets. Gói sai xác thực hoặc không hợp lệ bị bỏ im lặng; bộ đếm này không phải số đo mất gói Valorant. Không ghi khóa hay nội dung gói vào nhật ký.

Khi tích hợp, dùng CancellationToken để hủy tác vụ. Hủy rồi chờ RunAsync kết thúc trước khi Dispose server. Client.DisposeAsync gửi DISCONNECT theo cách cố gắng tối đa, dừng/chờ các tác vụ nền và giải phóng transport. Mỗi phía sở hữu transport được truyền vào nó.

## 8. Cấu hình server và client

config.example.json chỉ chứa UUID mẫu, IP ảo mẫu và chuỗi thay thế PSK cố ý không hợp lệ. Không dùng trực tiếp làm cấu hình chạy.

~~~powershell
Copy-Item config.example.json config.json
~~~

Chỉ thực hiện lệnh sao chép khi chưa có config.json để tránh ghi đè cấu hình riêng. Cấu hình thật phải được tạo/chỉnh cục bộ và không đưa lên Git.

ServerConfiguration.Load(path) đọc đối tượng JSON có mảng clients. Mỗi mục gồm:

| Trường | Ý nghĩa và kiểm tra |
|---|---|
| client_id | UUID khác rỗng, không trùng |
| psk_base64 | PSK riêng, Base64 hợp lệ, giải mã ít nhất 32 byte |
| virtual_ip | IPv4 unicast không trùng; loại bỏ dải đầu 0, 127, từ 224 trở lên và địa chỉ kết thúc .0/.255 |

Load từ chối tệp lớn hơn 1 MiB; danh sách phải có 1–4096 mục. IP ảo hiện chỉ là thông tin giao thức: không tạo hoặc gán IP lên adapter. Cổng lắng nghe và địa chỉ server không nằm trong JSON này; ứng dụng tích hợp cung cấp IPEndPoint khi tạo UdpDatagramTransport/TunnelClient.

Client nhận transport, địa chỉ server, clientId và PSK dạng byte[] qua constructor TunnelClient; không có tệp cấu hình client riêng hay trình chạy CLI cho tunnel. TunnelOptions và TimeProvider có thể được truyền qua constructor client/server. Chỉ cấp PSK tương ứng cho từng client, không phân phối toàn bộ cấu hình server.

.gitignore loại trừ config.json, config.*.json và *.secrets.json, ngoại trừ config.example.json. Không đưa PSK, thông tin VPS thật hoặc khóa riêng vào tài liệu và Git.

## 9. Cài đặt môi trường

Môi trường phát triển/kiểm thử hiện tại là Windows với .NET 10 SDK. Cần Git và kết nối NuGet để khôi phục gói kiểm thử lần đầu; chỉ .NET Runtime là chưa đủ để build.

Cài .NET 10 SDK từ [trang tải .NET của Microsoft](https://dotnet.microsoft.com/download/dotnet/10.0), sau đó kiểm tra:

~~~powershell
dotnet --list-sdks
~~~

Lấy mã nguồn vào thư mục mới, rồi chọn nhánh Phase 1:

~~~powershell
git clone --branch feat/phase1-tunnel https://github.com/nguyenttai121207/DualRoute.git
cd DualRoute
dotnet restore GameNetworkOptimizer.slnx
~~~

Tunnel Core dùng API không phụ thuộc Windows và nhắm net10.0; người dùng đã xác nhận bộ 2A trên Ubuntu LAN, chưa kiểm chứng VPS. Thư viện client Wintun riêng nhắm net10.0-windows.

## 10. Biên dịch và chạy

Chạy các lệnh từ thư mục chứa GameNetworkOptimizer.slnx:

~~~powershell
dotnet build GameNetworkOptimizer.slnx -c Release
dotnet run --project src/GameNetworkOptimizer -c Release -- --help
dotnet run --project src/GameNetworkOptimizer -c Release -- --public-ip 1.1.1.1 --interval-ms 1000 --timeout-ms 2000 --spike-ms 100 --data-dir data
~~~

Lệnh cuối chạy Network Monitor; nhấn Ctrl+C để dừng và đóng nhật ký. Các tùy chọn đều không bắt buộc, giá trị trên là mặc định. --public-ip nhận địa chỉ IP cụ thể, không phải tên miền; không nhận địa chỉ loopback/không xác định. Khoảng nghỉ tối thiểu 100 ms, timeout từ 1 đến 60000 ms, ngưỡng spike không âm.

**Thử tunnel:** dùng lệnh kiểm thử tích hợp tại mục 12. DualRoute.Tunnel là thư viện, không có Program hoặc lệnh dotnet run để khởi động server/client tunnel riêng.

## 11. Cách đọc kết quả Network Monitor

Monitor tìm gateway IPv4 của giao diện mà Windows chọn cho đích công cộng. Với đích IPv6, việc tìm gateway dùng tuyến IPv4 đến 1.1.1.1. Socket UDP Connect chỉ chọn tuyến, không gửi datagram. Nếu không tìm được gateway, monitor cảnh báo và chỉ đo đích công cộng. Adapter ảo/VPN có thể ảnh hưởng lựa chọn; cần khởi động lại khi gateway thay đổi.

Mỗi vòng đo tuần tự từng đích, rồi nghỉ đủ interval đã cấu hình. Đích chậm làm chu kỳ thực tế dài hơn interval; chưa đo đồng thời nhiều đích. Yêu cầu đang bị hủy không được tính là mất phản hồi.

| Số liệu | Cách tính hiện tại |
|---|---|
| Current RTT | RTT yêu cầu vừa hoàn tất; n/a nếu thất bại |
| Min/Max/Average | Nhỏ nhất, lớn nhất, trung bình cộng của RTT thành công |
| P95 | Sắp xếp RTT thành công, lấy phần tử thứ ceil(0,95 × n), đếm từ 1 |
| Jitter | Trung bình trị tuyệt đối chênh lệch giữa hai RTT thành công liên tiếp; bỏ qua yêu cầu thất bại |
| Packet loss | Số yêu cầu hoàn tất thất bại / tổng yêu cầu hoàn tất × 100%; bao gồm timeout |
| Spike | RTT thành công lớn hơn, không bằng, ngưỡng; giữ số lần, thời điểm và RTT cao nhất |

RTT/jitter dùng mili giây. Chưa có mẫu thì thống kê RTT là n/a; jitter cần ít nhất hai lần thành công; tỷ lệ mất phản hồi là 0% khi chưa có yêu cầu. Dashboard hiển thị từng đích, trạng thái ICMP, các số liệu và thời gian theo dõi; chưa có bộ phân loại sức khỏe mạng HEALTHY/DEGRADED.

CsvLogger tạo thư mục tự động và tệp UTF-8 có tên theo thời gian kèm GUID để tránh ghi đè. Các cột: TimestampUtc, SequenceNumber, TargetAddress, Status, LatencyMs, IsSpike. Thời điểm ghi theo UTC; RTT thất bại để trống. Mỗi dòng được flush; lỗi ghi làm monitor dừng và báo lỗi. Dòng đã flush được giữ lại, nhưng không bảo đảm chống mất dữ liệu do mất điện/hỏng phần cứng; dòng đang lỗi có thể chưa đầy đủ.

Monitor giữ toàn bộ mẫu RTT thành công và lịch sử spike trong bộ nhớ; bộ nhớ tăng theo thời gian. Chưa có giới hạn lưu giữ, xuất JSON hoặc cơ sở dữ liệu lịch sử cho monitor.

ICMP đến gateway/1.1.1.1 chỉ là chỉ báo chẩn đoán. Thiết bị có thể chặn hoặc giảm ưu tiên ICMP; thiếu phản hồi không chứng minh Internet bị ngắt. Đường đến đích đo có thể khác đường đến máy chủ game. Số liệu này và RTT PROBE tunnel không đại diện cho mất gói UDP hoặc RTT thực tế của Valorant.

## 12. Chạy kiểm thử tự động

~~~powershell
dotnet test GameNetworkOptimizer.slnx -c Release
dotnet test tests/DualRoute.Tunnel.Tests -c Release --filter Category=Integration
~~~

Bộ kiểm thử nền tảng có **61 ca**: 12 ca Network Monitor và 49 ca tunnel, tính cả Theory. Bước 2B bổ sung project test Windows riêng; kết quả mới nhất và các ca skip được ghi trong PROGRESS.md. Trên Linux, project test Windows không được chạy; kiểm thử TUN Linux vẫn bật riêng như bên dưới.

- Network Monitor: thống kê RTT, P95, jitter, tỷ lệ mất phản hồi, spike, tập rỗng, timeout/thất bại, kiểm tra tùy chọn và ghi CSV.
- Tunnel: định dạng/big-endian, giới hạn kích thước, HMAC, dẫn xuất khóa, mã hóa, gói bị sửa, số thứ tự, chống phát lại, giới hạn/hết hạn phiên chờ, thay phiên, cách ly client, KEEPALIVE, PROBE, hàng đợi và dừng tác vụ.
- Truyền tải giả lập hỗ trợ bỏ, nhân đôi, đảo thứ tự datagram; ManualTime điều khiển thời hạn bằng TimeProvider. Các phép chờ trong kiểm thử có giới hạn thời gian; không dùng Thread.Sleep.
- Bộ lọc Category=Integration chọn kịch bản hai client cho cả truyền tải giả lập và UDP thật. UDP thật bind loopback cổng 0 để hệ điều hành tự cấp cổng; A và B cùng kết nối, đo PROBE, gửi DATA từ A, ngắt A và kiểm tra B vẫn hoạt động.

Các kiểm thử không cần phiên chơi Valorant, khóa thật, VPS hay ping Internet. Kết quả kiểm thử cục bộ không thay thế đánh giá an ninh hoặc kiểm thử tải dài hạn.

## 13. Trạng thái triển khai và giới hạn

Đã có CLI ICMP, CSV, thư viện tunnel Phase 1, bước 2A và thư viện Wintun 2B. Người dùng xác nhận 61/61 tests của 2A trên Ubuntu LAN, gồm Linux TUN thật và cleanup; Codex không trực tiếp chạy Ubuntu. NAT thực tế, VPS, tải kéo dài và nghiệm thu Wintun thật vẫn chưa được xác minh.

Các giới hạn cần lưu ý:

- PSK không cung cấp forward secrecy: lộ PSK có thể ảnh hưởng bí mật phiên đã ghi lại; người có PSK có thể giả mạo client tương ứng.
- Chưa có giới hạn tần suất bắt tay theo từng client; client có PSK hợp lệ có thể chiếm dung lượng phiên chờ hữu hạn.
- Không tự truyền lại gói DATA hoặc HELLO bị mất; tự kết nối lại chỉ có một lần thử khi KEEPALIVE hết thời hạn.
- Có Linux TUN, thư viện Wintun và DATA hai chiều; chưa có host tunnel độc lập, nghiệm thu Wintun native, route Windows, tích hợp monitor/tunnel hoặc chứng nhận an toàn vận hành.
- CLI và thông báo lỗi của ứng dụng vẫn dùng tiếng Anh; việc Việt hóa này chỉ áp dụng cho tài liệu.

## 14. Tính năng chưa hoàn thành và định hướng Phase 2

Đây là định hướng đề xuất cho giai đoạn tiếp theo, chưa phải chức năng đã có hay cam kết phát hành:

1. Đóng gói chương trình server/client riêng và quy trình quản lý cấu hình bí mật.
2. Kiểm chứng Linux/VPS, NAT, thời gian chạy dài và tải nhiều client; rà soát an ninh trước khi triển khai rộng.
3. Nghiệm thu native Wintun (2B), rồi mới xét route (2C), CLI REPL (2D), thử game (2E).
4. Hoàn thiện giám sát: thống kê có giới hạn bộ nhớ, median/P99, trạng thái sức khỏe, lịch sử và xuất JSON.
5. Nghiên cứu tích hợp phép đo Direct/VPS tới cùng đích kiểm soát; xác định yêu cầu định tuyến và giao diện sau khi có dữ liệu kiểm chứng.

Chuyển tuyến tự động, Multipath, FEC và WinUI 3 chưa được triển khai. Không suy diễn rằng Phase 1 hiện đã tối ưu lưu lượng trò chơi.


## Bước 2A — Server Linux TUN và DATA hai chiều

**Phạm vi:** chỉ thư viện, không có Wintun, client app, route Windows hoặc REPL. Giữ nguyên wire format/khóa Phase 1. MTU lấy duy nhất từ Protocol.MaxPayloadSize (1350), không đặt thành 1380.

- ITunDevice tách thiết bị khỏi TunForwarder. TunnelServer.RunAsync(transport, tun, cancellationToken) sở hữu TUN và transport; không đọc ReceivedData từ nơi khác khi dùng overload này. Overload cũ vẫn giữ DATA thô và channel 64 để tương thích tests Phase 1.
- Client → TUN: kiểm tra IPv4, IHL, Total Length khớp số byte, kích thước; Source IP phải đúng Virtual IP của client và session_id còn hiện hành. Gói sai bị bỏ im lặng, có bộ đếm.
- TUN → client: kiểm tra IPv4 rồi tra Destination IP sang phiên hoạt động; mã hóa dưới khóa phiên, gửi qua cùng UDP transport tới endpoint của phiên. Không tự gia hạn idle chỉ vì server gửi.
- TunnelClient có một vòng giải mã; DATA đi vào channel 64 bằng TryWrite, không await channel đầy; KEEPALIVE/PROBE dùng channel khác. SendDataAsync chỉ giữ khóa session ngắn để mã hóa, không chờ khóa điều khiển đang đợi phản hồi. Gói DATA quá lớn bị bỏ và đếm.
- LinuxTunDevice hỗ trợ ABI Linux x64/arm64: mở /dev/net/tun nonblocking, tạo độc quyền tun0 với IFF_TUN/NO_PI/TUN_EXCL. Một worker chuyên dụng poll tối đa 50 ms/lần, có hàng đợi hữu hạn; hủy rồi chờ worker trước khi đóng fd. TUN không persistent nên đóng fd cuối cùng xóa thiết bị.
- Setup gọi ip qua ArgumentList, không shell, timeout 5 giây, kiểm tra exit code và đóng fd nếu lỗi. Cấu hình 10.77.0.1/24, MTU lấy từ hằng số, UP. Không nhận tun0 có sẵn, không sửa NAT/firewall hoặc default route.
- Khi một vòng UDP/TUN lỗi hoặc bị hủy: hủy các vòng còn lại, chờ kết thúc, giải phóng session, TUN và socket. Test thiết bị giả kiểm tra cả lỗi đọc, ghi bị chặn và MTU sai.

### Kiểm thử thủ công trên Ubuntu

Chạy trong bản checkout có code 2A với .NET 10 SDK, iproute2, /dev/net/tun và quyền root/CAP_NET_ADMIN. Không có tun0 của ứng dụng khác. Đọc trạng thái mạng trước, không tự bật/tắt:

~~~bash
sysctl net.ipv4.ip_forward
sudo iptables -t nat -S POSTROUTING
sudo ufw status verbose
sudo iptables -S ufw-user-forward
ip link show tun0
dotnet build GameNetworkOptimizer.slnx -c Release
dotnet test GameNetworkOptimizer.slnx -c Release
sudo env DUALROUTE_TEST_LINUX_TUN=1 dotnet test tests/DualRoute.Tunnel.Tests -c Release --filter Category=LinuxTun
ip link show tun0
~~~

Trước và sau test, ip link show tun0 phải báo không có thiết bị. Nếu đã có tun0 thì dừng và xác định chủ sở hữu; không xóa tùy tiện.

Test Linux bật riêng tạo tun0, kiểm tra MTU và việc không thể mở lại cùng tên, gửi ICMP Echo Request hợp lệ qua client giả vào TUN, nhận Echo Reply từ kernel qua DATA mã hóa, rồi xác nhận tun0 bị xóa. Test không cần NAT/Internet, không tạo route Windows và không đo game. Mặc định test này được **skip**, kể cả trên Linux nếu chưa bật biến môi trường.

**Nghiệm thu bổ sung:** người dùng xác nhận Ubuntu 192.168.1.200, .NET 10, bật DUALROUTE_TEST_LINUX_TUN=1: 61 PASS, 0 FAIL, 0 SKIP và cleanup tun0, ngày 2026-11-21. Đây là kết quả do người dùng xác nhận. NAT/Internet, Wintun thật, game và tải dài chưa được kiểm chứng.

Nguồn ABI đã đối chiếu: [Linux if_tun.h](https://github.com/torvalds/linux/blob/master/include/uapi/linux/if_tun.h), [if.h](https://github.com/torvalds/linux/blob/master/include/uapi/linux/if.h), [poll.h](https://github.com/torvalds/linux/blob/master/include/uapi/asm-generic/poll.h), [fcntl.h](https://github.com/torvalds/linux/blob/master/include/uapi/asm-generic/fcntl.h).

## Bước 2B — Thư viện Windows Wintun

`DualRoute.Client.Windows` target `net10.0-windows`, dùng lại `ITunDevice`, `Ipv4Packet`,
`TunnelClient` và `Protocol.MaxPayloadSize`. Không có CLI tunnel mới, routing game,
host route VPS, NAT hoặc cleanup route. `DualRoute.Wintun.TestHost` chỉ là fixture
integration test, không phải CLI sản phẩm và không phân phối cùng client.

### Tích hợp và vòng đời

Host phải chạy Windows AMD64 bằng process x64, quyền Administrator. Kết nối
`TunnelClient` trước rồi gọi `WintunDevice.CreateAsync(client, cancellationToken)`.
IP lấy từ `client.VirtualIp` trong HELLO_ACK có xác thực; chỉ nhận host trong
10.77.0.0/24, loại .0, .1 và .255. Không hardcode IP client trong runtime.

~~~csharp
// client đã ConnectAsync thành công; host sở hữu client và token Ctrl+C.
try
{
    await using var device = await WintunDevice.CreateAsync(client, cancellationToken);
    await new ClientTunForwarder(client, device).RunAsync(cancellationToken);
}
finally { await client.DisposeAsync(); }
~~~

`ClientTunForwarder` là reader duy nhất của `client.ReceivedData` trong phiên này.
Worker chuyên dụng chờ Wintun read event, tín hiệu ghi và stop; hai channel managed
giới hạn 64. Receive copy sang managed memory rồi release native buffer trong finally.
Allocate/copy/send không có await hoặc cancellation ở giữa; ring đầy thì drop/đếm.
IPv4 malformed, non-IPv4 và packet vượt MTU bị drop/đếm. Không log payload/khóa.

Vòng đời: journal Creating → CreateAdapter → xác minh identity/journal Created →
StartSession → IP Helper đặt IP /24, MTU từ hằng số 1350, administrative UP → worker.
Dispose: stop → join worker → EndSession → CloseAdapter → kiểm tra thiết bị biến mất →
xóa journal. Dispose nhiều lần dùng cùng một tác vụ. Lời gọi native CreateAdapter
không hủy tức thì; token được kiểm tra trước/sau, không bỏ mặc tác vụ tạo nền.

Gán IP có thể tạo connected route do Windows quản lý. Bước này không gọi API thêm,
sửa hoặc xóa route; không đặt gateway. Máy kiểm thử không nên có subnet 10.77.0.0/24
đang dùng bởi VPN khác. Reconnect đổi virtual IP làm cầu nối dừng và yêu cầu tạo lại adapter.

### DLL đã đóng gói

Nguồn: https://www.wintun.net/builds/wintun-0.14.1.zip.
ZIP đã được kiểm SHA-256 trước giải nén:
`07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51`.
Chỉ lấy `wintun/bin/amd64/wintun.dll`, `wintun/include/wintun.h` và `wintun/LICENSE.txt`.
Các file được giữ trong `src/DualRoute.Client.Windows/Native/`.
DLL và giấy phép tự copy ra output/publish lần lượt thành `wintun.dll` và `Wintun-LICENSE.txt`.

Runtime dùng đường dẫn tuyệt đối `AppContext.BaseDirectory/wintun.dll`, kiểm PE AMD64,
hash DLL `E5DA8447DC2C320EDC0FC52FA01885C103DE8C118481F683643CACC3220DAFCE`, rồi
LoadLibraryExW với DLL_LOAD_DIR và SYSTEM32. Không tìm DLL theo CWD/PATH, không tải mạng.
Thiếu DLL/sai hash: khôi phục bản đóng gói chính thức; thiếu quyền: chạy host elevated.
Module chỉ unload sau session/adapter; read event được mượn từ session, không CloseHandle.

### Journal và recovery chỉ dành cho adapter

`%ProgramData%/DualRoute/Wintun/wintun-state.json` độc lập với state.json Phase 3.
Schema version 1 gồm Owner, AdapterGuid, OperationId, Name, Stage và InstanceId.
GUID cố định; tên chứa operation ID ngẫu nhiên đã ghi trước khi tạo. Created bổ sung
device instance thực tế. State không chứa PSK. Thư mục/file chỉ cho Administrators
và SYSTEM ghi; owner/ACL không đáng tin hoặc reparse point khiến khởi tạo bị từ chối.
File lock FileShare.None giữ đến khi cleanup xong, ngăn hai instance cùng sở hữu adapter.

Mỗi lần ghi tạo file tạm mới, WriteThrough + Flush(true), rồi rename thay thế journal.
Crash trước rename giữ bản cũ hợp lệ; file tạm không được dùng làm bằng chứng ownership.
Cơ chế này không cam kết chống mọi sự cố mất điện/storage.

CreateAsync luôn recovery journal trước khi tạo adapter mới. Recovery xác minh schema,
GUID, tên có operation ID, Wintun hardware/type và InstanceId nếu đã ghi; không match
thì từ chối xóa, giữ state. Không có adapter tương ứng thì xóa journal hợp lệ; chạy lại an toàn.
Thiết bị còn sót đã xác minh được gỡ bằng SetupAPI DIF_REMOVE cho đúng instance.
Không xóa driver chung và không mở rộng sang cleanup route/CLI/app recovery.

Wintun 0.14.1 CloseAdapter gỡ adapter của handle CreateAdapter; CloseAdapter của
OpenAdapter không phải thao tác xóa. Kill/crash không chạy finally; Windows có thể
unplug software device nhưng còn PnP instance. Lần chạy tiếp theo recovery phần còn sót.
Cleanup lỗi hoặc Windows yêu cầu reboot thì giữ journal và báo lỗi. Không tự xóa journal
hay adapter lạ để ép chạy tiếp. Đóng process không đồng nghĩa đã nghiệm thu cleanup.

### Kiểm thử Windows

Unit tests dùng IWintunApi/IAdapterSystem giả; tái dùng fake TUN/transport hiện có qua
source link, không sửa tests Phase 1/2A. Test DLL thực tế chỉ load/resolve export rồi unload,
không tạo adapter. Integration thật mặc định skip, chạy tuần tự và cần Administrator:

~~~powershell
dotnet build GameNetworkOptimizer.slnx -c Release
dotnet test GameNetworkOptimizer.slnx -c Release

# PowerShell x64, Run as Administrator, trên máy/VM thử nghiệm:
$env:DUALROUTE_TEST_WINTUN = '1'
try {
    dotnet test tests/DualRoute.Client.Windows.Tests -c Release --filter Category=Wintun
} finally { Remove-Item Env:DUALROUTE_TEST_WINTUN }
~~~

Ca thật kiểm IP /24, MTU 1350, UP, UDP socket outbound qua Wintun rồi bơm phản hồi
đúng IP/port về socket và cleanup. Không phụ thuộc inbound ICMP Echo trên profile Public;
không đổi firewall hoặc thêm route. Policy chặn outbound UDP vẫn có thể làm test fail.
Ca crash tạo
test host có timeout, kill sau READY, recovery hai lần, xác minh adapter khác còn nguyên.
Fixture dùng journal riêng trong TEMP, vẫn dùng GUID DualRoute: không chạy cùng client thật.
Test host là process con chỉ trong kiểm thử, không thêm process vào đường chạy sản phẩm.

Chưa nghiệm thu trên Administrator: IP Helper/SetupAPI, packet/cleanup thật và kill/restart.
Reboot, đóng cửa sổ/Ctrl+C trên host thật, NAT/game/tải dài chưa kiểm chứng; không suy từ
unit tests sang kết quả đó. Trước/sau thử thủ công, dùng Get-NetAdapter -IncludeHidden,
Get-NetIPAddress và Get-NetIPInterface để kiểm tra đúng GUID, IP, MTU và thiết bị còn sót;
Get-NetRoute chỉ để đối chiếu, không sửa route. Thử reboot riêng trên VM: kill host lúc
READY → reboot → chạy lại cùng đường dẫn journal → dừng sạch → kiểm tra adapter biến mất.

Nguồn native: [Wintun 0.14.1 header](https://git.zx2c4.com/wintun/tree/api/wintun.h?h=0.14.1),
[Windows SDK netioapi.h](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/shared/netioapi.h),
[SetupDiCallClassInstaller](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdicallclassinstaller),
[LoadLibraryExW](https://learn.microsoft.com/en-us/windows/win32/api/libloaderapi/nf-libloaderapi-loadlibraryexw).

## Giấy phép

Dự án dùng giấy phép MIT; xem [LICENSE](LICENSE).
