# Sunllo DeskPair

[English](../../README.md) · [繁體中文](README.zh-Hant.md) · [简体中文](README.zh-Hans.md) ·
[日本語](README.ja.md) · [한국어](README.ko.md) · [Deutsch](README.de.md) ·
[Français](README.fr.md) · **Español** ·
[Português (Brasil)](README.pt-BR.md) · [Русский](README.ru.md)

> Esta es una traducción de [README.md](../../README.md). Si no coinciden, vale la versión en inglés.

Un sistema de escritorio remoto multiplataforma (Windows / macOS / Linux) escrito en C# / .NET 10.

- **Escritorio** (Avalonia) — el equipo que controla y el controlado en una sola aplicación.
- **Teléfonos** (Kotlin Multiplatform, Android e iOS) — el lado que controla, en un teléfono.
- **Rendezvous** — registro de ID, presencia y señalización para atravesar NAT (hole punching).
- **Relay** — retransmite TCP entre equipos que no pueden conectarse directamente.

Las cuentas son opcionales: vinculan una computadora o un teléfono a una persona y mantienen igual la lista de
computadoras guardadas en todos sus dispositivos. Son un servicio que funciona en `deskpair.app`, al que las
aplicaciones llegan por HTTPS (`/api/v1`); su código no se publica.

La arquitectura se inspira en [RustDesk](https://github.com/rustdesk/rustdesk), pero usa su propio protocolo
(mensajes protobuf, AES-256-GCM de extremo a extremo, identidades ECDSA P-256). Consulta `docs/architecture.md`; los
documentos de `docs/` están escritos en chino tradicional.

## Capturas de pantalla

La aplicación de escritorio en Windows. Las imágenes las dibuja `tools/DeskPair.Tools.Screenshots` a partir de las
propias ventanas de la aplicación, y las computadoras, personas y direcciones que aparecen son inventadas.

<table>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/remote-session.png" width="100%" alt="Una sesión remota"><br>
      Controlando otra computadora; cada una a la que estás conectado tiene su propia pestaña.
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/file-transfer.png" width="100%" alt="Transferencia de archivos"><br>
      Transferencia de archivos: esta computadora a la izquierda, la otra a la derecha.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/home.png" width="100%" alt="La ventana principal"><br>
      El ID y la contraseña de un solo uso de esta computadora, y las computadoras a las que te conectaste hace poco.
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/devices.png" width="100%" alt="Dispositivos"><br>
      Dispositivos guardados en grupos, y cuáles están en línea.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/terminal.png" width="100%" alt="Una terminal remota"><br>
      Un shell en otra computadora, ejecutándose con la cuenta indicada arriba.
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/incoming-request.png" width="100%" alt="Una solicitud de conexión"><br>
      Alguien pide conectarse: la persona frente a esta computadora acepta o rechaza, y elige qué permitir.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/history.png" width="100%" alt="Historial de conexiones"><br>
      Quién se ha conectado a esta computadora y qué se le permitió hacer.
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/settings-security.png" width="100%" alt="Configuración de seguridad"><br>
      Configuración de seguridad: quién puede conectarse y cómo lo demuestra.
    </td>
  </tr>
</table>

## Descarga

Cada versión está en la [página de versiones](https://github.com/Sunllo/DeskPair/releases/latest) y en
[deskpair.app/download](https://deskpair.app/download), con un SHA-256 para cada archivo (`SHA256SUMS`).

| Sistema | Archivos |
|---|---|
| Windows 10 1809 o posterior | x64, ARM64 y x86 (32 bits): para cada uno, un instalador (`.msi`) y un `.zip` portátil, con nombres como `DeskPair-<version>-win-x64.msi`. Con firma de código; mientras una versión nueva no haya ganado reputación, SmartScreen puede seguir preguntando antes de la primera ejecución. |
| macOS 13 o posterior | `DeskPair-<version>-arm64.dmg` (Apple silicon), `-x86_64.dmg` (Intel). Firmados y notarizados por Apple. |
| Linux, glibc 2.31 o posterior | x64, ARM64 y ARM de 32 bits (ARMv7): para cada uno, un `.deb`, un `.rpm`, un paquete de Arch y un `.tar.gz`. |

En Windows, el instalador coloca DeskPair en Archivos de programa y lo añade al menú Inicio y a Configuración ›
Aplicaciones, desde donde también se puede desinstalar; la aplicación instala sola las versiones siguientes en
cuanto un administrador lo aprueba. El `.zip` funciona desde donde lo descomprimas y se actualiza solo. Para
instalar sin ninguna pregunta, por ejemplo en muchos equipos a la vez:

```
msiexec /i DeskPair-<version>-win-x64.msi /qn
```

En Linux, instala el paquete de tu distribución con sus propias herramientas (se muestran los nombres de x64):

```
sudo apt install ./deskpair_<version>_amd64.deb                              # Debian, Ubuntu, Mint, Raspberry Pi OS
sudo dnf install ./deskpair-<version>-1.x86_64.rpm                            # Fedora, RHEL
sudo zypper install --allow-unsigned-rpm ./deskpair-<version>-1.x86_64.rpm    # openSUSE
sudo pacman -U deskpair-<version>-1-x86_64.pkg.tar.zst                        # Arch, Manjaro
```

Un paquete instala DeskPair en `/usr/lib/deskpair`, deja `deskpair` disponible en la ruta y lo agrega al menú de
aplicaciones. Cuando sale una versión nueva, la aplicación te avisa e instalas el paquete nuevo de la misma manera.
El `.tar.gz` funciona en cualquier distribución desde donde lo descomprimas y se actualiza solo, igual que las
versiones de Windows y macOS.

Las aplicaciones para teléfono, que controlan una computadora, todavía no están en las tiendas.

## Compilación

```
dotnet build DeskPair.slnx
dotnet test DeskPair.slnx
```

Requiere el SDK de .NET 10 (`global.json`). El video usa el codificador del sistema operativo (Media Foundation en
Windows). Este repositorio no incluye ningún códec H.264 por software —H.264 está patentado aparte del código fuente
BSD-2 de OpenH264—, pero puedes aportar uno; consulta `native/openh264/README.md`.

## Equipo controlado y aplicación de escritorio

DeskPair es un único ejecutable. Al abrirlo muestra tu ID y tu contraseña de un solo uso, te permite conectarte a
otras computadoras y ejecuta el motor del equipo controlado en el mismo proceso: abrir la aplicación es lo que hace
que esta computadora se pueda controlar, y cerrarla es lo que lo detiene.

```
# la aplicación
dotnet run --project src/DeskPair.Desktop

# solo el motor, sin ventana: para una máquina sin sesión de escritorio
dotnet run --project src/DeskPair.Desktop -- --server
```

**El acceso desatendido** —poder conectarse cuando la computadora está bloqueada o nadie ha iniciado sesión— es un
rol que se instala desde Configuración › Seguridad › Accesible mientras está bloqueada: un servicio en Windows, un
agente de launchd en macOS y un daemon de root en Linux. Consulta `docs/unattended-windows.md` y
`docs/unattended-linux.md`. Sin él, activa Configuración › General › **Iniciar cuando inicie sesión** para que la
computadora sea accesible después de reiniciar e iniciar sesión.

En macOS, ejecuta la aplicación desde su paquete `.app`: el permiso de grabación de pantalla y de accesibilidad se
concede al paquete, y un ejecutable iniciado desde una terminal usa en su lugar el de la terminal. Por eso allí
`--server` es solo para pruebas.

Para usar tus propios servidores, indica el servidor rendezvous y su clave pública en Configuración (o en
`config.json`); las conexiones directas a `host:21118` no necesitan ningún servidor.

## Inicio rápido (línea de comandos, sin ventana)

```
# 1. servidores (predeterminado: rendezvous udp+tcp/21116, nat-test 21115, http 21114; relay tcp/21117)
dotnet run --project src/DeskPair.Rendezvous
dotnet run --project src/DeskPair.Relay -- --Relay:HttpPort=21124

# 2. leer la clave del servidor en la que deben confiar los clientes
curl http://127.0.0.1:21114/key

# 3. equipo controlado (muestra su ID de 9 dígitos y su contraseña temporal)
dotnet run --project tools/DeskPair.Tools.PeerCli -- host --server 127.0.0.1 --key <KEY> --password secret

# 4. equipo que controla, en otra terminal; las líneas escritas se envían como chat
dotnet run --project tools/DeskPair.Tools.PeerCli -- connect <ID> --server 127.0.0.1 --key <KEY> --password secret
```

Las conexiones por ID prueban primero la dirección de la red local (misma IP pública), luego la perforación de NAT
por TCP y después el relay; `--force-relay` (PeerCli) / `ForceRelay` omite los caminos directos. Consulta
`docs/nat-test-matrix.md`.

Docker: `docker compose -f deploy/docker-compose.yml up --build` (red del host; las notas están en el archivo).
Para poner los servidores en producción: `deploy/README.md`.

## Licencia

DeskPair es software libre bajo la [GNU Affero General Public License, versión 3](../../LICENSE)
(`AGPL-3.0-only`), que cubre por igual las aplicaciones y los servidores de este repositorio. Lo importante si
ofreces los servidores a otras personas: la sección 13 te pide ofrecerles el código fuente de lo que realmente
ejecutas, con tus cambios incluidos.

Los componentes de terceros conservan sus propias licencias; la aplicación los enumera en Configuración › Acerca de DeskPair.
