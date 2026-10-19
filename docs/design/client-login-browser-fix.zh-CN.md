# 客户端登录：把二维码换成浏览器授权（真机联调发现）

> 2026-10-19 服务端真机联调后的结论。范围：`windows/`（Windows 客户端）与 `admin-console/`（卖家后台）。
> 服务端**不需要改**，本文只讲客户端与后台。

---

## 1. 结论先说

`GET /v1/auth/session/:ticket/qr` 返回的二维码**扫不通**。要改成用系统浏览器打开响应里的 `authorizeUrl`。

登录流程走不通的根因不是 bug，是**两种二维码不是一回事**。

## 2. 两种二维码的区别

| | 内容 | 手机扫了会怎样 |
| --- | --- | --- |
| 服务端 `/qr` 渲染的 | `renderQR(session.AuthorizeURL)` —— 整个 `open.weixin.qq.com/connect/qrconnect?appid=…` URL 编成的图 | 微信把它当**普通链接**，在手机内打开该页面 → 页面在手机上渲染不出 PC 登录 → 提示「不允许在手机中扫码」→ **回调永远不来** |
| 微信自己签发的 | `open.weixin.qq.com/connect/qrcode/<uuid>`，嵌在 qrconnect 页面的 `<img class="js_qrcode_img web_qrcode_img">` 里 | 微信识别为登录码 → 用户确认授权 → 回调 `redirect_uri` |

关键在于扫第一张码时**微信把它当链接在手机里打开**，于是落回一个手机上无法完成 PC 登录的页面，形成死循环。

微信那张真码只能靠**浏览器打开 `authorizeUrl`** 拿到——它嵌在那个页面的 HTML 里。

### 不需要手机就能验证

```bash
curl -sH 'User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)' \
     "$WECHATBRIDGE_LICENSE_SERVER/../v1/auth/session" >/dev/null   # 先建票
# 从响应里取 authorizeUrl，然后：
curl -sH 'User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)' "<authorizeUrl>" \
  | grep -oE 'src="/connect/qrcode/[^"]*"'
# → src="/connect/qrcode/091cvS0N0kGVkl2P"   ← 这才是能扫的那张
```

## 3. 联调证据

服务端日志，2026-10-19 08:39–08:45：

- **08:39:15** 有人用浏览器直接打开 `/v1/auth/wechat/callback?code=xxx&state=ticket`（字面量占位）→ **400**。**回调端点本身是好的**，伪造 state 被正确拒绝。
- 整段时间内**真实回调一次都没有**，nginx 与服务端两侧计数均为 **0** —— 微信在跳转之前就中止了。
- AppID/Secret 单独验证有效（`cgi-bin/token` 换到 access_token），**排除凭据问题**。
- 改用「浏览器打开 `authorizeUrl` → 手机扫微信那张码」后，**08:44:46 回调成功**，随后双令牌交付、激活登记落库、卖家后台签发全部跑通。

## 4. 客户端要改的（`windows/`）

### 4.1 三处，涉及两个文件

| 位置 | 现状 | 要改成 |
| --- | --- | --- |
| `Activation/AccountController.cs:111` | `FetchQrAsync` 拉 PNG | 不再拉 |
| `Activation/LoginWindow.xaml.cs:76-80` | `QrImage.Source = Decode(_controller.QrPng)` | 折叠，改提示文案 |
| `Activation/LoginWindow.xaml.cs:87-89` | `AuthorizeText` 是纯文本 `TextBlock`（`LoginWindow.xaml:67`） | 可点击，拉起浏览器 |

**已有的零件全都在，不用新增任何服务**：

- `_controller.AuthorizeUrl` 已经在 `AccountController.cs:112` 填好了
- `AuthorizeText` 已经在 `LoginWindow.xaml.cs:87` 显示它
- 拉起浏览器有先例：`MainViewModel.cs:953`（`UseShellExecute = true`）

### 4.2 拉起浏览器

```csharp
// 参考 MainViewModel.cs:953 的既有写法
var psi = new ProcessStartInfo(_controller.AuthorizeUrl) { UseShellExecute = true };
Process.Start(psi);
```

`UseShellExecute = true` 是必须的：`false` 会让 .NET 尝试用 HTTP 客户端而不是系统默认浏览器。

### 4.3 文案

现在 `L10n.Text("请用微信扫描二维码。")` 与倒计时都建立在「页面里有码」这个前提上，要一起改。加新文案记得同时改代码里的中文字符串 + 在 `Core/Localization/en.json` 补一条（`LocalizationTests` 会断言两边占位符索引集合一致）。

倒计时仍然有用——`state` 过期后必须重新建票。

## 5. 卖家后台要改的（`admin-console/`）

同一个错配，`js/app.js:302` 同样在渲染 `qrImageUrl`。

**但它已经有一条可用的路径**：`index.html:29` 的 `<a id="login-authorize" target="_blank">` 指向 `authorizeUrl`，`app.js:305` 会把 href 设上。点它就能走通。

要改的只是**别把坏码摆在显眼位置**——现在 224px 的坏码在正中，「在微信中打开」在下面，文案还写着「在微信中打开」（实际是浏览器，有误导）。

> 部署侧已经做过临时处置（隐藏坏码 + 改文案），副本在 `/opt/wechatbridge/admin-console`，改动未进仓库，等本节正式修完即可撤掉。

## 6. 服务端部署现状（供联调参考）

| | |
| --- | --- |
| 地址 | `https://ocbot.aixifs.com/v1/*` |
| 环境变量 | `WECHATBRIDGE_LICENSE_SERVER=https://ocbot.aixifs.com/` |
| **结尾斜杠不能少** | `HttpActivationTransport.cs:46` 专门处理：base 不带尾斜杠时 `new Uri("https://host/api", "v1/auth/session")` 解析成 `https://host/v1/auth/session`，路径段被静默丢弃，报出来是 404 而非「配置错了」 |
| 卖家后台 | `https://ocbot.aixifs.com/admin/`，与 `/v1/*` 同源 |

### 端到端已跑通的六步

扫码 → 回调 → 双令牌交付 → 激活登记落库 → 卖家后台签发 → **用客户端内嵌公钥验签通过**。

最后一步不能省：只跑到第五步会漏掉「公钥与签发私钥不配对」这类问题，而它在服务端侧的表现是**一切正常**。

现成样本：`act_93e302d5f878131e08795f21` → `lic_b14ea9a398a87074c41a36fc`，`plan=buyout`、`exp=null`、`ent=[PRO]`。

### 手工验这张许可证时的两个陷阱

- 签名是 **64 字节 raw `r‖s`**（`internal/license/crypto.go` 的 `signRaw`），不是 DER。`openssl dgst -verify` 要 DER，直接喂会报 `Error verifying data`，看起来像私钥不匹配。得先转 `SEQUENCE{INTEGER r, INTEGER s}`。
- canonical 每行都带 `key=` 前缀（`license.go:115`），无尾随换行，空段用 `-`，`ent` 大写排序去重。凭印象拼很容易少写前缀，那样算出的是不同的字节。

## 7. 一条方法论

**「编译通过 + 单测全绿」证明不了登录流程能用。**

这个错配在开发环境下完全不可见：客户端连不上、服务端日志只剩轮询、没有任何错误提示。任何写着「二维码登录」的端到端流程，都值得拿真机扫一次再标 ✅。

判据也不该是「进程起来了」，而是**全链路跑通一次**——见 §6 的六步。

---

## 8. 更正 §1 与 §2：图是能扫的，缺的是「问的那一半」（2026-10-20）

> 本文 §1 说「`/qr` 返回的二维码**扫不通**」，§2 说那张真码「**只能靠浏览器打开
> `authorizeUrl` 拿到**」。两句话都要改。

### 8.1 `/qr` 现在渲的是真码

`ab9121e` 起，服务端会先抓 `authorizeUrl` 指向的那个页面，从里面取出
`/connect/qrcode/<uuid>` 再渲图。所以 §2 那张表里「服务端 `/qr` 渲染的」
一行描述的已经是旧行为了。

图**能扫**。2026-10-19 线上 27 张取过 `/qr` 图的票全部停在 pending，
而这 27 次里手机那侧的确认是**成功了的**——不是图坏。

### 8.2 缺的那一半在哪儿

微信 PC 登录的「手机已确认」不是手机推给服务端的，是**PC 上那个页面自己
问出来的**：qrconnect 页面里 `setTimeout(r,2e3)` 每 2 秒轮询一次自己的
长轮询端点

```
/connect/l/qrconnect?uuid=…&last=…&f=xml
```

微信答出 `redirect_url` 时它才跳 `redirect_uri`。**code 是这一轮长轮询换来的。**
我们自己渲染一张静态图，没有那段循环，于是没有人去问，code 签不下来，
回调一次都不会来。

所以 §1 该改成：「`/qr` 的图能扫，扫完也确认了，但没有人去问微信『确认了
没有』，于是换不来 code。」

### 8.3 「只能靠浏览器」这句话太强了

2026-10-20 实测：**那个长轮询端点谁都能问**，不需要浏览器会话。

```
$ curl -sH 'User-Agent: <桌面 UA>' \
    "https://long.open.weixin.qq.com/connect/l/qrconnect?uuid=…&last=…&f=xml"
window.wx_errcode=408;window.wx_code='';
```

408 是「还没确认，继续等」——正是页面每 2 秒拿到的那一个答案；一次调用阻塞
约 15 秒才作答。页面拿到 405 时做的是
`redirect_uri + "?code=" + wx_code + "&state=" + state`，而 `redirect_uri`
与 `state` 服务端自己拼 `authorizeUrl` 时就写好了，所以服务端自己问出 405
之后拿到的回调与页面逐字节相同。

也就是说：**服务端自己问也能把这次登录做完。** 这条路本分支实现过又撤掉了
（远端分支没有服务端改动，合并时按「用远端方案」的要求舍弃），
但事实记在这里，免得下一个人重新推一遍。

**唯一真的搬不动的是「免扫码直登」**：微信页面上的「微信快捷登录」按钮
POST 到 `https://localhost.weixin.qq.com:{port}/api/authorize`——打的是
**用户自己机器上的微信客户端**。这一段服务端永远替不了。

### 8.4 当前的处置

后台与客户端都走**浏览器授权**那条路（`92a7f78`），后台页面不摆二维码。
理由是它已经在生产里跑通过（`auth_sessions` 里客户端与后台都有 authorized
的票），而服务端自己问那条路**没有经过真机验证**——按 §7 那条方法论，
「编译通过 + 单测全绿」证明不了登录流程能用。

**判据没变**：全链路六步（扫码 → 回调 → 双令牌 → 激活登记 → 后台签发 →
客户端内嵌公钥验签）要拿真机跑通一次。
