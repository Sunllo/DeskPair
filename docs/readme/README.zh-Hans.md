# Sunllo DeskPair

[English](../../README.md) · [繁體中文](README.zh-Hant.md) · **简体中文** ·
[日本語](README.ja.md) · [한국어](README.ko.md) · [Deutsch](README.de.md) ·
[Français](README.fr.md) · [Español](README.es.md) ·
[Português (Brasil)](README.pt-BR.md) · [Русский](README.ru.md)

> 本文译自 [README.md](../../README.md)，两者不一致时以英文版为准。

用 C# / .NET 10 编写的跨平台（Windows／macOS／Linux）远程桌面系统。

- **桌面程序**（Avalonia）——控制端和被控端在同一个程序里。
- **手机应用**（Kotlin Multiplatform，Android 和 iOS）——手机上的控制端。
- **Rendezvous**——ID 注册、在线状态和 NAT 打洞的信令。
- **Relay**——为无法直连的两端中继 TCP。

账号是可选的：它把电脑或手机关联到一个人，并让各设备上保存的电脑列表保持一致。账号是在 `deskpair.app`
运营的服务，应用通过 HTTPS（`/api/v1`）访问；这项服务的代码不公开。

架构参考了 [RustDesk](https://github.com/rustdesk/rustdesk)，但使用自己的协议（protobuf 消息、端到端
AES-256-GCM、ECDSA P-256 身份）。详见 `docs/architecture.md`；`docs/` 下的文档都用繁体中文撰写。

## 截图

Windows 上的桌面程序。这些图片由 `tools/DeskPair.Tools.Screenshots` 根据程序自己的窗口绘制，图中的电脑、人名和地址都是虚构的。

<table>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/remote-session.png" width="100%" alt="远程会话"><br>
      控制另一台电脑；每台已连接的电脑都有自己的标签页。
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/file-transfer.png" width="100%" alt="文件传输"><br>
      文件传输：左边是这台电脑，右边是对方。
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/home.png" width="100%" alt="主窗口"><br>
      这台电脑的 ID 和一次性密码，以及最近连接过的电脑。
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/devices.png" width="100%" alt="设备列表"><br>
      设备列表按分组排列，并显示哪些在线。
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/terminal.png" width="100%" alt="远程终端"><br>
      另一台电脑上的 shell，以顶部标明的账号运行。
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/incoming-request.png" width="100%" alt="连接请求"><br>
      有人请求连接时，这台电脑前的人选择接受或拒绝，并勾选允许的权限。
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/history.png" width="100%" alt="连接记录"><br>
      谁连接过这台电脑，以及当时被允许做什么。
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/settings-security.png" width="100%" alt="安全设置"><br>
      安全设置：谁可以连接，以及如何证明身份。
    </td>
  </tr>
</table>

## 下载

每个版本都在 [Releases 页面](https://github.com/Sunllo/DeskPair/releases/latest)和
[deskpair.app/download](https://deskpair.app/download)，每个文件都附有 SHA-256（`SHA256SUMS`）。

| 系统 | 文件 |
|---|---|
| Windows 10 1809 及以上 | `DeskPair-<version>-win-x64.zip`、`-win-arm64.zip`、`-win-x86.zip`（32 位）。尚未进行代码签名，首次运行时 SmartScreen 会先询问。 |
| macOS 13 及以上 | `DeskPair-<version>-arm64.dmg`（Apple 芯片）、`-x86_64.dmg`（Intel）。已签名并经过 Apple 公证。 |
| Linux，glibc 2.31 及以上 | x64、ARM64 和 32 位 ARM（ARMv7）：各有 `.deb`、`.rpm`、Arch 软件包和 `.tar.gz`。 |

在 Linux 上，用发行版自带的工具安装对应的软件包（以下是 x64 的文件名）：

```
sudo apt install ./deskpair_<version>_amd64.deb                              # Debian, Ubuntu, Mint, Raspberry Pi OS
sudo dnf install ./deskpair-<version>-1.x86_64.rpm                            # Fedora, RHEL
sudo zypper install --allow-unsigned-rpm ./deskpair-<version>-1.x86_64.rpm    # openSUSE
sudo pacman -U deskpair-<version>-1-x86_64.pkg.tar.zst                        # Arch, Manjaro
```

软件包会把 DeskPair 安装到 `/usr/lib/deskpair`，命令行可以直接运行 `deskpair`，应用程序菜单里也会出现。
有新版本时应用会提示你，再用同样的方式安装新的软件包。`.tar.gz` 在任意发行版上解压到哪里都能运行，并且会
自行更新，和 Windows、macOS 版一样。

手机应用（用来控制电脑）尚未上架。

## 构建

```
dotnet build DeskPair.slnx
dotnet test DeskPair.slnx
```

需要 .NET 10 SDK（`global.json`）。视频使用操作系统的编码器（Windows 上是 Media Foundation）。本仓库不附带
软件 H.264 编解码器——H.264 的专利与 OpenH264 的 BSD-2 源代码是两回事——但可以自行提供，见
`native/openh264/README.md`。

## 被控端和桌面程序

DeskPair 只有一个可执行文件。打开它会显示你的 ID 和一次性密码，可以连接其他电脑，同一个进程里也运行被控端
引擎——所以打开应用就是让这台电脑可以被控制，关闭应用就停止。

```
# 应用本身
dotnet run --project src/DeskPair.Desktop

# 只有引擎、没有窗口：用于没有桌面会话的机器
dotnet run --project src/DeskPair.Desktop -- --server
```

**无人值守访问**——电脑锁定或无人登录时也能连接——是需要另外安装的角色，在「设置 › 安全 › 锁定时仍可连接」中
安装：Windows 上是服务，macOS 上是 launchd agent，Linux 上是 root daemon。见 `docs/unattended-windows.md` 和
`docs/unattended-linux.md`。未安装时，打开「设置 › 常规 › **登录时自动启动**」，重启并登录后即可连接。

在 macOS 上请从 `.app` 包运行应用：屏幕录制和辅助功能的授权是授予这个应用包的，从终端启动的可执行文件用的
则是终端的授权。因此在 macOS 上，`--server` 只是测试用的角色。

要使用自己的服务器，在「设置」（或 `config.json`）中填写 rendezvous 服务器及其公钥；直接连接 `host:21118`
则不需要任何服务器。

## 快速上手（无窗口的命令行）

```
# 1. 服务器（默认：rendezvous udp+tcp/21116、nat-test 21115、http 21114；relay tcp/21117）
dotnet run --project src/DeskPair.Rendezvous
dotnet run --project src/DeskPair.Relay -- --Relay:HttpPort=21124

# 2. 读取客户端必须信任的服务器密钥
curl http://127.0.0.1:21114/key

# 3. 被控端（打印 9 位数字 ID 和临时密码）
dotnet run --project tools/DeskPair.Tools.PeerCli -- host --server 127.0.0.1 --key <KEY> --password secret

# 4. 控制端，在另一个终端运行；输入的每一行都会作为聊天消息发送
dotnet run --project tools/DeskPair.Tools.PeerCli -- connect <ID> --server 127.0.0.1 --key <KEY> --password secret
```

按 ID 连接时，依次尝试局域网地址（公网 IP 相同时）、TCP 打洞，最后才是 relay；`--force-relay`（PeerCli）／
`ForceRelay` 会跳过直连。见 `docs/nat-test-matrix.md`。

Docker：`docker compose -f deploy/docker-compose.yml up --build`（使用 host 网络，说明写在文件里）。
正式部署服务器：见 `deploy/README.md`。

## 许可证

DeskPair 是自由软件，采用 [GNU Affero 通用公共许可证第 3 版](../../LICENSE)（`AGPL-3.0-only`），本仓库中的应用和
服务器都适用。如果你为他人运行服务器，需要注意第 13 条：你必须让这些用户能够获得你实际运行的程序的源代码，
包括你所做的修改。

第三方组件保留各自的许可证；应用在「设置 › 关于 DeskPair」中列出它们。
