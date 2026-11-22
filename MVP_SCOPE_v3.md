# DualRoute -- MVP Scope (v3, bản đơn giản nhất)

> **Mục tiêu:** phần mềm chạy trên Windows, nhiều máy cùng kết nối **một VPS Singapore**;
> mình và bạn chơi thử, **thấy được sự khác biệt** giữa Direct và qua VPS, và **lưu lại bằng chứng**.
>
> **Chạy được → Chơi được → Đo được → Chứng minh được.**

---

## 1. Sáu tiêu chí của MVP

| # | Tiêu chí | Đạt khi |
|---|---|---|
| 1 | **Chạy được** | App chạy trên Windows 10/11; nhiều máy cùng kết nối một VPS, session tách biệt |
| 2 | **Kết nối được** | Client kết nối, giữ kết nối và tự nối lại khi mất mạng |
| 3 | **Chơi được** | Chơi game qua VPS không crash app/game, không làm hỏng mạng máy; không can thiệp game/anti-cheat |
| 4 | **Tắt sạch** | Stop/Exit là dừng hết task, đóng socket, gỡ route/adapter do app tạo |
| 5 | **Đo được** | Test Mode (chức năng riêng, chỉ bật khi test) ghi CSV số liệu thật |
| 6 | **Chứng minh được** | Có CSV, log, ảnh chụp, video và một báo cáo so sánh Direct với qua VPS |

> Về "không bị chặn / không bị ban": **không thể bảo đảm trước**. MVP chỉ cam kết không can thiệp game hay anti-cheat (mục 7); nếu game cảnh báo hoặc chặn thì dừng thử và ghi lại.

---

## 2. Cách hoạt động (giữ đơn giản)

```text
 MÁY WINDOWS (nhiều máy)                                    VPS SINGAPORE
 Game ─► route (chỉ IP game đã cấu hình) ─► Wintun
      ─► Tunnel Client ── UDP, mã hóa ────────────────────► Tunnel Server
                                                              │ giải mã → TUN → NAT
 Game ◄─ Wintun ◄─ Tunnel Client ◄───────────────────────────┘      │
                                                                      ▼
                                                              Internet / Server game
                                                              (chỉ thấy IP của VPS)
```

1. Client tạo adapter ảo (Wintun) và **chỉ route các IP/CIDR game đã cấu hình** vào adapter đó. Phần còn lại của máy đi bình thường.
2. Gói game đi vào Wintun, client mã hóa (ChaCha20-Poly1305) và gửi UDP tới VPS.
3. VPS giải mã, đưa vào TUN, NAT ra Internet tới server game.
4. Gói trả về đi ngược lại: NAT → TUN → VPS tra đúng client → mã hóa → gửi về client → Wintun → game.

**Nhiều client, session tách biệt:**
- Mỗi client có `client_id` + khóa riêng (PSK) khai trong file cấu hình của server. Client lạ bị bỏ im lặng, nên VPS không thành open relay.
- Server cấp mỗi client **một virtual IP cố định** (ví dụ `10.77.0.2`, `10.77.0.3`, ...) khai sẵn trong cấu hình. Gói về có IP đích nào thì server gửi đúng client đó.
- Server bỏ gói có IP nguồn khác virtual IP của session.
- Một client ngắt chỉ làm server dọn session của client đó.

**Mã hóa (tóm tắt, tránh lỗi nonce):** mỗi lần kết nối, client và server bắt tay (`HELLO` / `HELLO_ACK`) trao đổi `client_nonce` và `server_nonce`; server cấp `session_id` 8 byte, khóa phiên hai chiều (`c2s` và `s2c`) dẫn xuất bằng HKDF từ PSK + `client_nonce || server_nonce`. Nonce = `seq` tăng dần, nên khởi động lại không bao giờ dùng lại nonce.

Khung gói: `version | type | client_id | session_id | seq | ciphertext+tag`, với `type` = `HELLO`, `HELLO_ACK`, `DATA`, `PROBE`, `PROBE_REPLY`, `KEEPALIVE`. MTU adapter ảo đặt bằng `Protocol.MaxPayloadSize` (**1350** byte).

**Hai lỗi hay gặp cần tránh:**
- Phải có **host route tới IP VPS qua gateway gốc**, nếu không gói tunnel bị route ngược vào chính tunnel.
- Cần quyền Administrator (tạo adapter, route); báo lỗi rõ nếu thiếu.

**Chế độ:**

| Mode | Ý nghĩa |
|---|---|
| `Direct` | Không route game vào tunnel (để đối chứng) |
| `Tunnel` | Route IP game vào tunnel, đi qua VPS |
| `Duplicate` *(giai đoạn B, mục 8)* | Gửi cùng một datagram qua hai đường tới VPS, VPS loại gói trùng |

---

## 3. Chức năng tối thiểu

### Windows Client

| ID | Chức năng | Hoàn thành khi |
|---|---|---|
| C1 | `start` / `stop` / `status` (CLI) | Hiện trạng thái `Connecting / Connected / Reconnecting / Failed` và lỗi rõ ràng |
| C2 | Kết nối VPS, keepalive, tự nối lại | Mất mạng rồi có lại thì tự nối lại |
| C3 | Wintun + route IP game + host route VPS + chuyển `Direct`/`Tunnel` | Đổi mode được mà không phải khởi động lại máy |
| C4 | Quản lý task và cleanup (mục 4) | Stop/Exit/crash không để lại route/adapter |
| C5 | Test Mode + CSV (mục 5) | Bật/tắt độc lập, không làm đứt kết nối đang chơi |

### VPS Server

| ID | Chức năng | Hoàn thành khi |
|---|---|---|
| S1 | Nhận nhiều client, khóa riêng từng client, session riêng | Hai máy kết nối đồng thời, không lẫn dữ liệu |
| S2 | TUN + NAT + virtual IP theo client + chống giả mạo nguồn | Gói về đúng client |
| S3 | Trả `PROBE_REPLY`, dọn session idle | Một client ngắt không ảnh hưởng client khác |
| S4 | Chạy bằng systemd, firewall chỉ mở cổng UDP cần thiết | VPS khởi động lại thì dịch vụ tự chạy |

---

## 4. Task con và tắt sạch

- **Một process duy nhất**: tunnel, keepalive, Test Mode là các task bất đồng bộ dùng `CancellationToken`. Nếu bắt buộc phải chạy process con, đưa vào **Windows Job Object** để tắt app là tắt hết.
- Mỗi task có trạng thái `Starting / Running / Stopping / Stopped / Failed`.
- **Chống sót khi crash:** trước khi tạo adapter/route, ghi vào `state.json`. Khi khởi động luôn đọc file này để dọn phần sót của phiên trước. Có lệnh `dualroute cleanup` để dọn thủ công.
- Chỉ xóa route/adapter **do chính app tạo**.

**Trình tự Stop/Exit:** ngừng nhận việc mới → dừng Test Mode, flush CSV → dừng tunnel, đóng socket → gỡ route, đóng adapter → xóa `state.json`, ghi log cleanup.

**Phải test:** Stop thường; đóng cửa sổ; kill process; mất mạng; mất VPS; máy khởi động lại khi chưa cleanup. Dừng client A không được ảnh hưởng client B.

---

## 5. Test Mode và CSV

- Mặc định **tắt**; chỉ bật khi test; dừng đo mà không dừng kết nối chơi game.
- Ba loại probe:

| `probe_type` | Đo gì | Ghi chú |
|---|---|---|
| `ICMP-gateway` | Ping gateway WiFi | Cho biết lỗi nằm ở WiFi nội bộ hay ở xa hơn |
| `ICMP-target` | Ping một IP **nằm trong dải đã route** (IP server game, hoặc máy Singapore trả ping) | Ở `Direct` đi thẳng, ở `Tunnel` tự đi qua VPS, nên so sánh được hai mode |
| `UDP-tunnel` | `PROBE`/`PROBE_REPLY` giữa client và VPS | Chỉ có khi tunnel bật |

**CSV tối thiểu** (một dòng mỗi phép đo, giữ dữ liệu thô):

| Cột | Ý nghĩa |
|---|---|
| `timestamp_utc` | Thời điểm đo |
| `client_id` | Máy thực hiện đo |
| `mode` | Direct / Tunnel (/ Duplicate) |
| `probe_type` | Theo bảng trên |
| `target` | Địa chỉ đo |
| `seq` | Số thứ tự probe |
| `rtt_ms` | RTT, để trống nếu mất gói |
| `lost` | 0 hoặc 1 |

Jitter, trung vị, P95, tỉ lệ loss tính **sau** từ dữ liệu thô. Không có dữ liệu thì để trống, không tự tạo số liệu.

**Giới hạn phải ghi trong báo cáo:** ICMP và probe tới VPS **không phải ping game**. Bằng chứng cuối cùng là ping/cảm giác trong game (ảnh chụp, video) đối chiếu với CSV. Game server có thể không trả lời ICMP; khi đó dùng một máy Singapore khác làm `target`.

---

## 6. Cách chơi thử và lưu bằng chứng

**Quy trình**
1. Bắt đầu **một máy** có giám sát, rồi mới thêm máy thứ hai.
2. Chạy luân phiên **Direct, Tunnel, Direct, Tunnel** (xen kẽ), cùng khung giờ, cùng loại trận/khu vực game, Test Mode bật suốt.
3. Mỗi lượt ghi: giờ, mode, ISP, WiFi hay dây, ping hiển thị trong game, cảm nhận (giật/lag), có lỗi hay cảnh báo gì không.
4. Làm ít nhất vài lượt mỗi mode, vào cả giờ thấp điểm và giờ cao điểm.

**Bằng chứng cần lưu**

```text
Evidence/
├── CSV/         direct-*.csv  tunnel-*.csv
├── Logs/        client-a.log  client-b.log  server.log
├── Screenshots/ (ping trong game + trạng thái app)
├── Gameplay/    (video chơi thử)
└── Report/comparison.md
```

**Báo cáo `comparison.md`** gồm: điều kiện thử (ISP, WiFi, giờ, số lượt), bảng so sánh Direct và Tunnel (trung vị, P95, loss, số lần giật), ảnh/video, vấn đề gặp phải. **Không tuyên bố tunnel tốt hơn nếu dữ liệu không chứng minh;** nếu không khác biệt hoặc tệ hơn, đó vẫn là kết quả hợp lệ.

---

## 7. Điều kiện anti-cheat và tài khoản game

- Chỉ xử lý lưu lượng ở tầng mạng hệ điều hành.
- Không hook, inject, đọc/ghi bộ nhớ hay sửa tiến trình game; không vô hiệu hóa hoặc né anti-cheat.
- Đọc điều khoản hiện hành của nhà phát hành trước khi thử.
- **Thử sớm** (giai đoạn 2) xem game có chấp nhận adapter Wintun không; nếu có cảnh báo/chặn thì dừng và điều tra, không tìm cách vượt qua.
- Nên dùng tài khoản mà bạn chấp nhận rủi ro khi thử nghiệm.

---

## 8. Kế hoạch triển khai

| GĐ | Công việc | Kết quả |
|---|---|---|
| 1 | Giao thức, mã hóa, session, **nhiều client** chạy trên loopback/LAN với traffic giả (chưa cần Wintun) | Hai client giả kết nối cùng server, không lẫn dữ liệu; test đơn vị pass |
| 2 | Wintun + route (client), TUN + NAT (server, thử trên máy LAN/VM) + **thử sớm với game** | Ping/UDP đi qua tunnel; game không bắt lỗi adapter |
| 3 | Task con, cleanup, `state.json`, crash recovery | Mọi tình huống ở mục 4 sạch |
| 4 | Test Mode + CSV | Bật đo và đọc lại được CSV |
| 5 | Thuê VPS Singapore, chơi thử với bạn, thu bằng chứng | Có đủ thư mục `Evidence/` |
| B | *(Chỉ làm khi 1 đến 5 xong)* **Duplicate**: hai socket tới hai cổng VPS, gửi cùng một datagram, dedup bằng cửa sổ `seq` trượt ở cả hai phía; chế độ thứ ba trong báo cáo | So sánh Direct / Tunnel / Duplicate |

Chỉ thuê VPS ở GĐ 5, sau khi số liệu Test Mode trên mạng thật cho thấy lag đến từ tuyến đường chứ không chỉ từ WiFi nội bộ (gateway ổn nhưng đích xa xấu).

---

## 9. Checklist hoàn thành MVP

- [ ] **Chạy được:** chạy trên Windows; hai máy trở lên kết nối đồng thời một VPS, session độc lập; có hướng dẫn cài đặt.
- [ ] **Chơi được:** chơi thử có giám sát, không crash do app, không làm hỏng mạng; ghi lại mọi cảnh báo.
- [ ] **Tắt sạch:** Stop/Exit/crash không để sót task, socket, route, adapter; client này ngắt không ảnh hưởng client kia.
- [ ] **Đo được:** Test Mode bật/tắt độc lập; CSV có timestamp và nhãn loại probe, đọc lại được.
- [ ] **Chứng minh được:** có CSV thô, log client và server, ảnh, video, báo cáo so sánh nêu rõ giới hạn.

---

## 10. Không làm trong MVP

UI WinUI 3; tự dò IP game; Auto mode chọn đường; traceroute; dashboard biểu đồ; auto-update; tài khoản, thanh toán; nhiều VPS/khu vực; hỗ trợ nhiều game; tối ưu hiệu năng.

**Nguyên tắc:** chạy ổn định hơn đẹp; dùng thư viện có sẵn (Wintun, `ChaCha20Poly1305`, `HKDF` của .NET); không tạo process nếu không cần; mọi tài nguyên do app tạo phải có cách dọn; không tối ưu theo cảm giác mà theo số liệu.
