# PipeWire with nobody at a screen, for testing the half of Wayland screen sharing every desktop has in common: the
# shim, the buffers it hands over and the capturer that reads them. test-screen.c publishes a picture as a PipeWire
# video source, the way a compositor publishes a monitor; the compiler and PipeWire's headers build it and the shim
# from source inside, as the product builds the shim. See run.sh.
#
# The portal itself is not in here. GNOME's and KDE's ask their person before sharing, and the wlroots one (the one
# that would not ask) cannot capture without a GPU: xdg-desktop-portal-wlr 0.7 insists on dmabuf formats, which a
# headless compositor in a container does not have.
FROM ubuntu:24.04

RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      pipewire pipewire-bin wireplumber dbus dbus-user-session jq \
      libicu74 ca-certificates build-essential pkg-config libpipewire-0.3-dev \
 && rm -rf /var/lib/apt/lists/*

# PipeWire expects a user with a runtime directory, not root.
RUN useradd --create-home tester
USER tester
WORKDIR /home/tester
