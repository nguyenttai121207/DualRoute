# DualRoute — Tiến độ dự án

Cập nhật: 2026-11-21. Nguồn phạm vi: MVP_SCOPE_v3.md; quy tắc triển khai: AGENTS.md. Không ghi PSK/cấu hình bí mật vào nhật ký này.

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

## Bước 2A — Đã triển khai; Windows đạt, Linux TUN thật chờ kiểm chứng

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

### Chưa kiểm chứng

- Linux TUN native và cleanup trên Ubuntu thật, NAT/Internet, game, Wintun/route, tải dài.
- Bản build đa nền tảng và fake tests pass trên Windows không chứng minh TUN native đã chạy trên Linux.
- Xem README mục “Bước 2A” để chạy lệnh Linux bật riêng. Sau test phải kiểm tra tun0 đã biến mất; không xóa thiết bị có sẵn của người dùng.

## Tiếp theo

| Bước | Trạng thái |
|---|---|
| 2A Linux runtime | Chờ chạy test có quyền trên Ubuntu; Windows logic/tests đạt |
| 2B Client Wintun | Chưa triển khai |
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
