# Sunllo DeskPair

[English](../../README.md) · [繁體中文](README.zh-Hant.md) · [简体中文](README.zh-Hans.md) ·
**日本語** · [한국어](README.ko.md) · [Deutsch](README.de.md) ·
[Français](README.fr.md) · [Español](README.es.md) ·
[Português (Brasil)](README.pt-BR.md) · [Русский](README.ru.md)

> これは [README.md](../../README.md) の翻訳です。内容が食い違う場合は英語版が正しいものとします。

C# / .NET 10 で書かれた、クロスプラットフォーム（Windows／macOS／Linux）のリモートデスクトップシステムです。

- **デスクトップアプリ**（Avalonia）— 操作する側とされる側が一つのアプリにまとまっています。
- **スマートフォンアプリ**（Kotlin Multiplatform、Android と iOS）— スマートフォンから操作する側です。
- **Rendezvous** — ID の登録、オンライン状態、NAT ホールパンチングのシグナリング。
- **Relay** — 直接つながらない端末同士の TCP を中継します。

アカウントは任意です。コンピューターやスマートフォンを一人の人に結び付け、保存したコンピューターの一覧を各端末で
そろえます。アカウントは `deskpair.app` で運営しているサービスで、アプリは HTTPS（`/api/v1`）で接続します。
このサービスのコードは公開していません。

アーキテクチャは [RustDesk](https://github.com/rustdesk/rustdesk) を参考にしていますが、独自のプロトコル
（protobuf メッセージ、エンドツーエンドの AES-256-GCM、ECDSA P-256 による識別）を使っています。詳しくは
`docs/architecture.md` を参照してください。`docs/` 以下のドキュメントは繁体字中国語で書かれています。

## ダウンロード

各リリースは[リリースページ](https://github.com/Sunllo/DeskPair/releases/latest)と
[deskpair.app/download](https://deskpair.app/download) にあり、各ファイルの SHA-256（`SHA256SUMS`）も添えています。

| システム | ファイル |
|---|---|
| Windows 10 1809 以降 | `DeskPair-<version>-win-x64.zip`、`-win-arm64.zip`、`-win-x86.zip`（32 ビット）。まだコード署名していないため、初回の実行前に SmartScreen が確認を求めます。 |
| macOS 13 以降 | `DeskPair-<version>-arm64.dmg`（Apple シリコン）、`-x86_64.dmg`（Intel）。署名済みで、Apple の公証を受けています。 |
| Linux（glibc 2.31 以降） | x64、ARM64、32 ビット ARM（ARMv7）のそれぞれに `.deb`、`.rpm`、Arch パッケージ、`.tar.gz` があります。 |

Linux では、ディストリビューション標準のツールで対応するパッケージをインストールします（x64 のファイル名で示します）。

```
sudo apt install ./deskpair_<version>_amd64.deb                              # Debian, Ubuntu, Mint, Raspberry Pi OS
sudo dnf install ./deskpair-<version>-1.x86_64.rpm                            # Fedora, RHEL
sudo zypper install --allow-unsigned-rpm ./deskpair-<version>-1.x86_64.rpm    # openSUSE
sudo pacman -U deskpair-<version>-1-x86_64.pkg.tar.zst                        # Arch, Manjaro
```

パッケージは DeskPair を `/usr/lib/deskpair` にインストールし、コマンドラインから `deskpair` で起動でき、
アプリケーションメニューにも表示されます。新しいバージョンが出るとアプリが知らせるので、同じ方法で新しい
パッケージをインストールしてください。`.tar.gz` はどのディストリビューションでも展開した場所から実行でき、
Windows 版や macOS 版と同じく自分で更新します。

コンピューターを操作するスマートフォンアプリは、まだストアで公開していません。

## ビルド

```
dotnet build DeskPair.slnx
dotnet test DeskPair.slnx
```

.NET 10 SDK（`global.json`）が必要です。映像は OS のエンコーダー（Windows では Media Foundation）を使います。
このリポジトリにはソフトウェアの H.264 コーデックは含まれていません。H.264 の特許は OpenH264 の BSD-2 の
ソースとは別の話だからです。ただし自分で用意することはできます。`native/openh264/README.md` を参照してください。

## ホストとデスクトップアプリ

DeskPair は一つの実行ファイルです。起動すると自分の ID とワンタイムパスワードが表示され、ほかのコンピューターに
接続でき、同じプロセスの中でホストのエンジンも動きます。つまり、アプリを開いている間このコンピューターは
操作を受けられる状態になり、閉じるとそれが止まります。

```
# アプリ
dotnet run --project src/DeskPair.Desktop

# ウィンドウなしでエンジンだけ：デスクトップのセッションがないマシン向け
dotnet run --project src/DeskPair.Desktop -- --server
```

**無人アクセス**（コンピューターがロック中、または誰もサインインしていないときにも接続できること）は、
「設定 › セキュリティ › ロック中も接続できるようにする」からインストールする役割です。Windows ではサービス、
macOS では launchd エージェント、Linux では root デーモンになります。`docs/unattended-windows.md` と
`docs/unattended-linux.md` を参照してください。インストールしない場合は、「設定 › 一般 › **サインイン時に起動する**」
をオンにすると、再起動してサインインした後に接続できます。

macOS では、アプリを `.app` バンドルから起動してください。画面収録とアクセシビリティの許可はバンドルに対して
与えられ、ターミナルから起動した実行ファイルにはターミナルの許可が使われます。そのため macOS では、
`--server` はテスト用の役割です。

自前のサーバーを使うには、「設定」（または `config.json`）で rendezvous サーバーとその公開鍵を指定します。
`host:21118` への直接接続にはサーバーは要りません。

## クイックスタート（ウィンドウなしのコマンドライン）

```
# 1. サーバー（既定：rendezvous udp+tcp/21116、nat-test 21115、http 21114、relay tcp/21117）
dotnet run --project src/DeskPair.Rendezvous
dotnet run --project src/DeskPair.Relay -- --Relay:HttpPort=21124

# 2. クライアントが信頼すべきサーバー鍵を読み出す
curl http://127.0.0.1:21114/key

# 3. ホスト（9 桁の ID と一時パスワードを表示）
dotnet run --project tools/DeskPair.Tools.PeerCli -- host --server 127.0.0.1 --key <KEY> --password secret

# 4. 操作する側（別のターミナルで）。入力した行はチャットとして送られる
dotnet run --project tools/DeskPair.Tools.PeerCli -- connect <ID> --server 127.0.0.1 --key <KEY> --password secret
```

ID で接続すると、LAN のアドレス（同じグローバル IP のとき）、TCP ホールパンチング、最後に relay の順に試します。
`--force-relay`（PeerCli）／`ForceRelay` を使うと直接接続を飛ばします。`docs/nat-test-matrix.md` を参照してください。

Docker：`docker compose -f deploy/docker-compose.yml up --build`（ホストネットワークを使用。注意点はファイル内に
あります）。本番でサーバーを動かすには `deploy/README.md` を参照してください。

## ライセンス

DeskPair は [GNU Affero 一般公衆利用許諾書 第 3 版](../../LICENSE)（`AGPL-3.0-only`）のもとで提供される
自由ソフトウェアで、このリポジトリのアプリにもサーバーにも同じように適用されます。他の人のためにサーバーを
運用する場合に重要なのは第 13 条です。その人たちに、実際に動かしているプログラムのソースコードを、
変更点も含めて提供する必要があります。

サードパーティのコンポーネントはそれぞれのライセンスに従います。アプリの「設定 › DeskPairについて」に一覧があります。
