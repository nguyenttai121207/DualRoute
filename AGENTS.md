# DualRoute — Quy tắc cho AI

Nguồn chuẩn: `MVP_SCOPE_v3.md` (phạm vi), `README.md` §5–6 và `src/DualRoute.Tunnel/Protocol.cs` (giao thức). Không chép lại đặc tả ở đây; nếu mâu thuẫn, code Phase 1 thắng.

## Lộ trình
Phase 1 xong. Phase 2 gồm 2A Server TUN → 2B Client Wintun → 2C Route → 2D CLI REPL → 2E thử game. Chỉ làm đúng bước được giao. Không làm state.json tổng thể, CSV Test Mode, UI, auto-update, dò IP game. Ngoại lệ 2B được người dùng duyệt: wintun-state.json chỉ quản lý ownership/recovery adapter DualRoute.

## Phase 2B — Windows Wintun
- Thư viện net10.0-windows triển khai ITunDevice; không đổi core Linux/giao thức. ClientTunForwarder chỉ đọc TunnelClient.ReceivedData một nơi, dùng VirtualIp server cấp sau handshake; không dùng 10.77.0.1 cho client.
- Chỉ Wintun 0.14.1 AMD64 chính thức, kiểm tra SHA-256 archive trước giải nén, giữ giấy phép. DLL cạnh exe, nạp đường dẫn tuyệt đối; không tìm theo CWD/PATH hoặc tải khi runtime.
- Worker chuyên dụng; trả receive buffer đúng một lần, không await giữa allocate/send; dừng/join worker trước EndSession/CloseAdapter. Read event thuộc session, không CloseHandle.
- GUID cố định, journal versioned wintun-state.json ghi bền trước tạo adapter; ownership phải khớp identity thực tế. State hỏng/không rõ ownership: từ chối xóa. Recovery idempotent, chỉ adapter DualRoute; không xóa driver dùng chung.
- CloseAdapter của handle CreateAdapter gỡ thiết bị; handle OpenAdapter không có cùng quyền sở hữu. Cleanup phải xác minh adapter đã gỡ, giữ journal khi lỗi.
- Không routing game, host route, default gateway, NAT, route cleanup, CLI cleanup hay recovery tổng thể Phase 3. Không tự commit/push theo yêu cầu triển khai 2B.

## Môi trường
- Mọi project target .NET 10 (client: `net10.0-windows`). Không dùng NuGet mới nếu chưa hỏi.
- Server: Ubuntu, 192.168.1.200:5000/udp, card Internet `enp2s0`, tunnel 10.77.0.0/24, gateway 10.77.0.1.
- Điều kiện NAT phải kiểm tra, không giả định: `sysctl net.ipv4.ip_forward`, `iptables -t nat -S POSTROUTING`, `ufw status verbose`, `iptables -S ufw-user-forward`.
- MTU tunnel = `Protocol.MaxPayloadSize` (1350), lấy từ MỘT hằng số dùng chung cho cả hai đầu. Muốn đổi → hỏi trước (phải sửa wire format và test Phase 1).

## Quy trình mỗi bước
1. Đọc các file liên quan trước khi viết. Không đoán tên API, kiểm tra header/tài liệu (Wintun, .NET).
2. Đưa kế hoạch ngắn: file tạo/sửa, cách làm, rủi ro, cách test. Chờ xác nhận MỘT lần cho cả bước.
3. Diff nhỏ nhất đủ chạy. Không refactor ngoài phạm vi. Commit riêng cho từng bước, không dùng lệnh git phá hủy.
4. Chạy `dotnet build GameNetworkOptimizer.slnx -c Release` và `dotnet test GameNetworkOptimizer.slnx -c Release`. Nếu môi trường không chạy được, nói rõ phần nào CHƯA kiểm chứng; tuyệt đối không báo pass khi chưa chạy.
5. Báo cáo cuối: file đã đổi, kết quả build/test, lệnh test thủ công, những gì chưa kiểm chứng.
6. Sau khi hoàn thành và test pass mỗi bước 2A → 2E, Phase 3, 4, 5, tự động cập nhật PROGRESS.md trong cùng đợt thay đổi: trạng thái, ngày/mốc/commit nếu có, danh sách file tạo/sửa, lệnh và kết quả build/test (pass/fail/skip, nền tảng), kiểm thử thủ công và phần chưa kiểm chứng. Không chờ yêu cầu nhắc lại. Phân biệt kết quả tự chạy với kết quả do người dùng xác nhận; không đánh dấu Linux/VPS đã đạt chỉ vì tests Windows pass.

## Phong cách code
- Ngắn, thẳng, ưu tiên BCL và thành phần có sẵn: `IDatagramTransport`, `TunnelServer`/`TunnelClient`, `TunnelOptions`, `ServerConfiguration`, `TimeProvider`, `Channel<T>`. Chỉ thêm abstraction khi có ≥2 implementation hoặc cần cho test (`ITunDevice`: Linux, Wintun, in-memory).
- Không comment lặp lại code; chỉ giải thích "vì sao". Không tính năng đầu cơ.
- `DualRoute.Tunnel` đang `TreatWarningsAsErrors`: build phải 0 warning. Không dùng `Thread.Sleep` trong test.
- Mọi task bất đồng bộ nhận `CancellationToken`; tắt phải sạch (socket, thread, fd, adapter, route).
- Thông báo lỗi cho người dùng: rõ, có hướng xử lý (tiếng Việt hoặc Anh). Log được phép ghi địa chỉ/độ dài gói, KHÔNG ghi khóa hay payload.

## Gói IPv4 (mọi nơi ghi/đọc TUN/Wintun)
Kiểm tra: độ dài ≥ 20, version == 4, IHL hợp lệ, Total Length ≤ số byte đọc, bỏ gói non-IPv4. Server: Source IP phải khớp virtual IP của `client_id`, sai thì bỏ im lặng; gói về tra client theo Destination IP. Gói vượt `MaxPayloadSize` thì bỏ và đếm, không ném lỗi.

## An toàn khi đụng mạng
- Không bao giờ thêm/sửa/xóa route `0.0.0.0/0`. Từ chối CIDR cấu hình mà bao gồm IP server hoặc gateway.
- Host route `/32` tới server: on-link (next hop 0.0.0.0, đúng interface) nếu server cùng subnet; qua default gateway nếu ở xa. Chọn interface bằng route thực tế tới server, không parse `route print`.
- Chỉ gỡ route/adapter do chính app tạo; cleanup trong `finally` và khi Ctrl+C.
- Process ngoài (`ip`, `route`, `netsh`) chỉ ở bước setup/cleanup: `ArgumentList`, không qua shell, có timeout, kiểm tra exit code. Ưu tiên API (IP Helper, `NetworkInterface`) khi rẻ.

## Bẫy đã biết
- Từ 2A: DATA client được phân kênh không chặn; SendDataAsync không giữ khóa điều khiển. Hàng đợi DATA client/server vẫn chỉ 64 chỗ, drop khi đầy. Khi chạy TUN, TunForwarder là bên đọc duy nhất của TunnelServer.ReceivedData.
- Linux: `read()` trên tun không hủy được; dùng nonblocking + `poll` timeout. Windows: đọc Wintun bằng `WintunGetReadWaitEvent`, không chặn thread pool; `wintun.dll` x64 đặt cạnh exe; adapter dùng GUID cố định.
- Socket UDP trên Windows: tắt `SIO_UDP_CONNRESET`, nếu không `ReceiveFrom` ném lỗi khi nhận ICMP port-unreachable.

## Hỏi trước khi làm
Đổi hằng số/wire format, thêm NuGet, thêm process ngoài, đụng `GameNetworkOptimizer`, đổi/xóa test hiện có, thêm tính năng ngoài bước được giao.

## Bảo mật & Definition of Done
- Không commit `config.json`, PSK, thông tin VPS. Ví dụ dùng giá trị `FAKE_...`.
- Xong khi: build 0 warning, toàn bộ test (cũ + mới) pass, có lệnh test thủ công, và sau Ctrl+C không còn route/adapter/process sót.
