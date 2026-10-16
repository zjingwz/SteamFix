# SteamFix

> 专治**校园网**下的 Steam 下载疑难杂症：提示「**无法连接到内容服务器**」、
> 进度卡死不动、速度一直 0 bps。

![platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-blue)
![size](https://img.shields.io/badge/binary-23%20KB-brightgreen)
![license](https://img.shields.io/badge/license-MIT-green)

---

## 症状

**校园网 / 学校 WiFi 下**，Steam 下载游戏时：

- 提示红色错误 **「无法连接到内容服务器」**
- 进度卡在某个字节数再也不动
- 下载速度图飙一下就掉到 **0 bps**
- 日志里反复出现 `Update delayed for 300 secs`
- 关掉重开、重启电脑、在 Steam 里换下载地区，都没用

**典型特征**：同一个游戏、同一台电脑，换成手机热点之类的其它网络就正常了。
这说明 Steam 客户端和电脑本身都没毛病 —— 问题出在校园网的 DNS 上。

## 原因

校园网的 DNS（部分运营商 DNS 也一样）是这套毛病的根源。

Steam 下载时，数据分片（chunk）的请求会被 CDN **重定向**到另一个节点，例如：

```
st.dl.eccdnx.com  --302-->  gstore.val.manlaxy.com
dl.steam.clngaa.com  --302-->  dl1.steam.clngaa.com
```

而校园网 DNS 对这批域名经常给出**大量已经失效的 IP**，甚至完全不返回。Steam 会拿着
这堆 IP 挨个去连，每个都要静默等 60 秒超时，全部轮完就报「无法连接到内容服务器」，
然后等 300 秒重试 —— 于是永远卡住。

实测一组数据（某高校校园网）：

| 域名 | 校园 DNS | 阿里 DNS |
|---|---|---|
| `gstore.val.manlaxy.com` | **0 个 IP**（解析失败） | 12 个 |
| `st.dl.eccdnx.com` | 17 个（一半连不上） | 8 个 |
| `xz.pphimalayanrt.com` | 19 个（大量死 IP） | 9 个 |

反过来验证：用手动挑出来的可用节点去拉那个「卡住」的分片，**1.7 秒就下完了**。
所以东西下得下来，只是 Steam 挑错了服务器。

## 它做什么

1. 用公共 DNS（阿里 223.5.5.5 / 腾讯 119.29.29.29 / 百度 180.76.76.76 / 114）解析出全部候选 IP
2. **逐个实测**：先测端口能不能连上，再发一个 HTTP 请求看有没有真实响应
3. 把当时最快的 2 个节点写进 `hosts`（写之前自动备份）
4. 刷新 DNS 缓存，可选自动重启 Steam

不是把某一批 IP 硬编码进去 —— 每次都是**现场实测**，所以换了宿舍、换了教学楼、
换了学校、IP 过期了，都能自己找到当时可用的节点。

> 虽然起因是校园网，但对任何「DNS 返回一堆失效 CDN IP」的网络（部分运营商宽带、
> 公共 WiFi）同样有效。

## 使用

### 方式一：直接下载（推荐）

到 [Releases](../../releases/latest) 页面下载 `SteamFix.exe`，**双击运行**，
在弹出的 UAC 窗口点「是」，按提示走完即可。

- 免安装，**不需要** Python / .NET 运行时 / 任何依赖（用的是 Windows 10/11 自带的 .NET Framework 4.x）
- 体积 23 KB，跑完就退出，**不常驻后台、不占资源**

### 方式二：命令行

```bat
SteamFix.exe               正常运行（探测 + 写入 hosts，需要管理员）
SteamFix.exe --dry-run     只探测不修改，先看看有哪些节点可用
SteamFix.exe --restore     撤销：删掉写进 hosts 的内容
SteamFix.exe --nopause     结束后不等待按键（给脚本调用）
```

`--dry-run` 输出的样子：

```
第 2 步 / 4  逐个实测哪些节点真的能连上并返回数据 ...
   共 56 个候选, 并发探测中 (最长约 7 秒) ...
   探测完成: 23 / 56 个节点可用。

   实测结果 (只列能连上的):
     [采用] dl.steam.clngaa.com       43.242.156.2     28ms     HTTP 302 正常
     [采用] dl1.steam.clngaa.com      36.99.183.119    43ms     HTTP 200 正常
     [采用] st.dl.eccdnx.com          125.44.162.220   16ms     HTTP 200 正常
     ...
```

## 效果

同一台机器、同一个校园网，修复前后对比（数据来自 Steam 自己的 `logs/content_log.txt`）：

| | 修复前 | 修复后 |
|---|---|---|
| `st.dl.eccdnx.com` | **0 Bytes, 0.00 Mbps** | **1.87 GB / 331s → 45 Mbps** |
| `dl.steam.clngaa.com` | **0 Bytes, 0.00 Mbps** | 459 MB / 327s → 11 Mbps |
| 命中率 | 0 Hits / 大量 Misses | **6120 Hits / 0 Misses (100%)** |
| 错误行 | 满屏 `Failed to issue HTTP request` | **0 条** |
| 瞬时速率 | 0.002 ~ 0.013 Mbps | 22 ~ **106 Mbps** |

最终剩余 5.6 GB 全部下完并成功安装。

## 从源码编译

只有一个 `.cs` 文件，用系统自带的 C# 编译器即可（无需安装 Visual Studio / SDK）：

```bat
build.cmd
```

它会调用 `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` 把 `src\SteamFix.cs`
编译成 `SteamFix.exe`。

> 注意：系统自带的 csc 是 **C# 5** 编译器，所以源码里没有字符串插值、`?.`、`nameof`
> 这些新语法。

## 注意事项

- 只修改 `hosts` 里 6 个 Steam CDN 域名，**不动其它任何东西**
- 不碰 Steam 账号、不碰游戏文件、不改注册表
- 原 `hosts` 会备份到 `%LOCALAPPDATA%\SteamFix\hosts.bak_日期时间`
- 运行日志在 `%LOCALAPPDATA%\SteamFix\steamfix.log`
- 写入时会**自动识别原 hosts 的编码**（GBK / UTF-8 / UTF-16）并原样回写，不会破坏原有中文注释
- 完全绿色：先跑一次 `--restore`，再删掉 exe 即可

## 常见问题

**Q：只有校园网会这样吗？**
校园网最常见 —— 学校 DNS 的缓存和调度策略往往最差，还常有自助换 IP / 换出口、限速
之类的策略。但部分运营商宽带、公共 WiFi 也会犯同样的毛病，只要属于「DNS 返回一堆
失效 CDN IP」都适用。

**Q：会被封号吗？有风险吗？**
不会。这是国内常见的「hosts 大法」，只是把几个 CDN 域名指向实测可用的服务器 IP，
不涉及账号、不涉及游戏文件。

**Q：为什么每次运行选出来的 IP 不一样？**
因为每次都是现场实测。CDN 有几十个节点，可用性随时在变，每次挑当时最快的那几个才是对的。

**Q：杀毒软件报警？**
单文件小程序，自提权 + 改 hosts 的组合可能触发启发式误报。可以先加白名单，或者先跑
`--dry-run` 看看它要做什么（不会修改任何文件）。

**Q：提示「没找到任何可用节点」怎么办？**
说明当前网络对 Steam 内容服务器做了彻底封锁（有些学校会直接封 CDN 网段）。
这种情况请改用游戏加速器（雷神 / UU / 奇游等），或者换一个网络（比如手机热点）再试。

**Q：以后又出问题了？**
重新运行一次即可。CDN 的 IP 会变，重新实测一遍就好。

## 适用系统

Windows 10 / 11（32 位和 64 位都可以）。

> 只针对 Windows 客户端。Linux / macOS 上的 Steam 如果遇到同类问题，思路一样，
> 可以参照 `src/SteamFix.cs` 里的域名列表自己改 hosts。

## License

[MIT](LICENSE)
