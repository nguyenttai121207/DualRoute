# DualRoute — Chẩn đoán mạng và giao thức tunnel Phase 1

DualRoute là dự án xây dựng nền tảng chẩn đoán mạng và thử nghiệm đường truyền cho trò chơi trực tuyến trên Windows 11. Phiên bản hiện tại gồm một ứng dụng dòng lệnh đo ICMP và một thư viện giao thức UDP có mã hóa, xác thực, quản lý phiên và nhiều máy khách.

**Trạng thái:** đã triển khai phần lõi tunnel Phase 1 và các kiểm thử cục bộ. Chưa có ứng dụng tunnel server/client độc lập, chưa chuyển tiếp lưu lượng Internet hay trò chơi và chưa kiểm chứng triển khai Linux/VPS. Không có bằng chứng rằng phiên bản này cải thiện ping hoặc mất gói của Valorant.

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

DATA hiện đi từ client đến server qua SendDataAsync; ứng dụng tích hợp đọc TunnelServer.ReceivedData. Server không chuyển tiếp nội dung này ra Internet. Chiều server về client hiện phục vụ phản hồi điều khiển đã mã hóa.

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

Tunnel Core dùng API không phụ thuộc Windows và nhắm net10.0; điều này chưa đồng nghĩa đã kiểm chứng chạy trên Linux hoặc VPS.

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

Bộ kiểm thử hiện có **37 ca**: 12 ca Network Monitor và 25 ca tunnel, tính cả các trường hợp dữ liệu của Theory.

- Network Monitor: thống kê RTT, P95, jitter, tỷ lệ mất phản hồi, spike, tập rỗng, timeout/thất bại, kiểm tra tùy chọn và ghi CSV.
- Tunnel: định dạng/big-endian, giới hạn kích thước, HMAC, dẫn xuất khóa, mã hóa, gói bị sửa, số thứ tự, chống phát lại, giới hạn/hết hạn phiên chờ, thay phiên, cách ly client, KEEPALIVE, PROBE, hàng đợi và dừng tác vụ.
- Truyền tải giả lập hỗ trợ bỏ, nhân đôi, đảo thứ tự datagram; ManualTime điều khiển thời hạn bằng TimeProvider. Các phép chờ trong kiểm thử có giới hạn thời gian; không dùng Thread.Sleep.
- Bộ lọc Category=Integration chọn kịch bản hai client cho cả truyền tải giả lập và UDP thật. UDP thật bind loopback cổng 0 để hệ điều hành tự cấp cổng; A và B cùng kết nối, đo PROBE, gửi DATA từ A, ngắt A và kiểm tra B vẫn hoạt động.

Các kiểm thử không cần phiên chơi Valorant, khóa thật, VPS hay ping Internet. Kết quả kiểm thử cục bộ không thay thế đánh giá an ninh hoặc kiểm thử tải dài hạn.

## 13. Trạng thái triển khai và giới hạn

Đã có CLI ICMP, CSV, thư viện tunnel Phase 1, cấu hình mẫu và kiểm thử nhiều client trên Windows loopback. Chưa kiểm chứng Linux/VPS, NAT thực tế hoặc vận hành tải kéo dài.

Các giới hạn cần lưu ý:

- PSK không cung cấp forward secrecy: lộ PSK có thể ảnh hưởng bí mật phiên đã ghi lại; người có PSK có thể giả mạo client tương ứng.
- Chưa có giới hạn tần suất bắt tay theo từng client; client có PSK hợp lệ có thể chiếm dung lượng phiên chờ hữu hạn.
- Không tự truyền lại gói DATA hoặc HELLO bị mất; tự kết nối lại chỉ có một lần thử khi KEEPALIVE hết thời hạn.
- Chưa có chương trình host tunnel độc lập, adapter ảo, chuyển tiếp dữ liệu, DATA ứng dụng hai chiều, tích hợp monitor/tunnel hoặc chứng nhận an toàn vận hành.
- CLI và thông báo lỗi của ứng dụng vẫn dùng tiếng Anh; việc Việt hóa này chỉ áp dụng cho tài liệu.

## 14. Tính năng chưa hoàn thành và định hướng Phase 2

Đây là định hướng đề xuất cho giai đoạn tiếp theo, chưa phải chức năng đã có hay cam kết phát hành:

1. Đóng gói chương trình server/client riêng và quy trình quản lý cấu hình bí mật.
2. Kiểm chứng Linux/VPS, NAT, thời gian chạy dài và tải nhiều client; rà soát an ninh trước khi triển khai rộng.
3. Thiết kế luồng DATA hai chiều, chính sách chuyển tiếp và kiểm soát truy cập.
4. Hoàn thiện giám sát: thống kê có giới hạn bộ nhớ, median/P99, trạng thái sức khỏe, lịch sử và xuất JSON.
5. Nghiên cứu tích hợp phép đo Direct/VPS tới cùng đích kiểm soát; xác định yêu cầu định tuyến và giao diện sau khi có dữ liệu kiểm chứng.

Chuyển tuyến tự động, Multipath, FEC và WinUI 3 chưa được triển khai. Không suy diễn rằng Phase 1 hiện đã tối ưu lưu lượng trò chơi.

## Giấy phép

Dự án dùng giấy phép MIT; xem [LICENSE](LICENSE).
