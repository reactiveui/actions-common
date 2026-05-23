# syntax=docker/dockerfile:1
#
# Prebuilt Certum SimplySign signing image.
#
# Bakes in everything the release sign job needs — SimplySign Desktop (~313 MB
# installer from Certum's slow EU host), the X/Qt/PKCS#11 runtime stack, jsign,
# and the .NET SDK — so a sign run pulls one image instead of downloading the
# installer and apt-installing ~30 packages every time.
#
# Base: modern .NET SDK on Ubuntu 24.04 (Noble) — gives `dotnet nuget verify`
# plus the exact apt package names the signing flow was validated against.
ARG DOTNET_TAG=10.0-noble
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_TAG}

# SimplySign Desktop Linux (Ubuntu) installer. No Renovate datasource exists for
# Certum's file host, so bump this manually when Certum ships a new version.
ARG SIMPLYSIGN_BIN_URL=https://files.certum.eu/software/SimplySignDesktop/Linux-Ubuntu/2.9.14-9.4.3.0/SimplySignDesktop-2.9.14-9.4.3.0-x86_64-prod-ubuntu.bin
# renovate: datasource=maven depName=net.jsign:jsign
ARG JSIGN_VERSION=7.4

ENV DEBIAN_FRONTEND=noninteractive

# Headless X (Xvfb + fluxbox), UI automation (xdotool/wmctrl), PKCS#11 tooling,
# Java for jsign, and the Qt5/WebKit + misc system libs SimplySign needs.
RUN apt-get update && apt-get install -y --no-install-recommends \
      xvfb x11-utils xdotool wmctrl fluxbox \
      opensc opensc-pkcs11 default-jre-headless unzip openssl ca-certificates curl python3 \
      libx11-6 libxext6 libxcomposite1 libxi6 libice6 libsm6 libxrender1 \
      libxcb1 libxau6 libpcsclite1 libglx-mesa0 libgl1 libxslt1.1 \
      libxkbcommon0 libxcb-xinerama0 libfontconfig1 libdbus-1-3 procps \
      libpulse-mainloop-glib0 libpulse0 libasound2t64 \
      libqt5webkit5 libqt5multimedia5 libqt5multimediawidgets5 libqt5quick5 \
      libqt5printsupport5 libqt5positioning5 libqt5sensors5 libqt5opengl5 \
      libgstreamer1.0-0 libgstreamer-plugins-base1.0-0 \
      libwoff1 libharfbuzz0b libhyphen0 libwebp7 libwebpdemux2 \
      libnss3 libxtst6 libxss1 libxrandr2 libxdamage1 libxfixes3 libxcursor1 \
      libegl1 libgbm1 libxkbcommon-x11-0 \
    && rm -rf /var/lib/apt/lists/*

# Install SimplySign Desktop. The bundled SimplySignDesktop_start wrapper
# hardcodes /opt/SimplySignDesktop/SimplySignDesktop, so the dist MUST live there
# (mirrors hpvb/certum-container). Extract to a temp dir, then move the dist into
# place and drop the installer + temp to keep the layer smaller.
RUN curl -fsSL -o /tmp/ss.bin "$SIMPLYSIGN_BIN_URL" \
    && mkdir -p /tmp/ss-extract \
    && (yes yes | sh /tmp/ss.bin --target /tmp/ss-extract || true) \
    && mv "$(find /tmp/ss-extract -maxdepth 1 -type d -name 'SSD-*-dist' | head -1)" /opt/SimplySignDesktop \
    && rm -rf /tmp/ss.bin /tmp/ss-extract \
    && test -x /opt/SimplySignDesktop/SimplySignDesktop

# jsign (signs .nupkg over PKCS#11).
RUN curl -fsSL -o /opt/jsign.jar "https://repo1.maven.org/maven2/net/jsign/jsign/${JSIGN_VERSION}/jsign-${JSIGN_VERSION}.jar"

# Contract consumed by the certum-sign action when it detects the prebuilt image:
# skip the apt/SimplySign/jsign install and use these baked-in paths.
ENV CERTUM_PREBUILT=1 \
    SS_DIST=/opt/SimplySignDesktop \
    JSIGN_JAR=/opt/jsign.jar \
    LD_LIBRARY_PATH=/opt/SimplySignDesktop

LABEL org.opencontainers.image.title="Certum SimplySign signer" \
      org.opencontainers.image.description="SimplySign Desktop + jsign + .NET SDK for headless NuGet signing on Linux" \
      org.opencontainers.image.source="https://github.com/reactiveui/actions-common"
