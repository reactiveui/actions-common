# syntax=docker/dockerfile:1
#
# Prebuilt Certum SimplySign signing image.
#
# Bakes in everything the release sign job needs — SimplySign Desktop (~313 MB
# installer from Certum's slow EU host), the X/PKCS#11 runtime stack, jsign,
# and the .NET 10 and 11 SDKs — so a sign run pulls one image instead of
# downloading the installer and apt-installing ~30 packages every time.
#
# Base: .NET 11 SDK on Ubuntu 26.04 (Resolute). SimplySign bundles its own Qt 5.9,
# whose WebKit links libxml2.so.2; Resolute ships only libxml2.so.16, so the xml
# stage builds the last .so.2 libxml2 and a libxslt against it.
ARG DOTNET_TAG=11.0-resolute

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_TAG} AS xml

ARG LIBXML2_VERSION=2.13.9
ARG LIBXML2_SHA256=a2c9ae7b770da34860050c309f903221c67830c86e4a7e760692b803df95143a
# libxslt 1.1.44 and later require libxml2 2.15.
ARG LIBXSLT_VERSION=1.1.43
ARG LIBXSLT_SHA256=5a3d6b383ca5afc235b171118e90f5ff6aa27e9fea3303065231a6d403f0183a

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update && apt-get install -y --no-install-recommends build-essential pkg-config xz-utils curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL -o /tmp/libxml2.tar.xz "https://download.gnome.org/sources/libxml2/${LIBXML2_VERSION%.*}/libxml2-${LIBXML2_VERSION}.tar.xz" \
    && echo "${LIBXML2_SHA256}  /tmp/libxml2.tar.xz" | sha256sum -c - \
    && mkdir /tmp/libxml2 \
    && tar -xJf /tmp/libxml2.tar.xz -C /tmp/libxml2 --strip-components=1 \
    && cd /tmp/libxml2 \
    && ./configure --prefix=/opt/xml --disable-static --without-python \
    && make -j"$(nproc)" install

RUN curl -fsSL -o /tmp/libxslt.tar.xz "https://download.gnome.org/sources/libxslt/${LIBXSLT_VERSION%.*}/libxslt-${LIBXSLT_VERSION}.tar.xz" \
    && echo "${LIBXSLT_SHA256}  /tmp/libxslt.tar.xz" | sha256sum -c - \
    && mkdir /tmp/libxslt \
    && tar -xJf /tmp/libxslt.tar.xz -C /tmp/libxslt --strip-components=1 \
    && cd /tmp/libxslt \
    && PKG_CONFIG_PATH=/opt/xml/lib/pkgconfig ./configure --prefix=/opt/xml --disable-static --without-python --without-crypto \
    && make -j"$(nproc)" install

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_TAG}

# SimplySign Desktop Linux (Ubuntu) installer. No Renovate datasource exists for
# Certum's file host, so bump this manually when Certum ships a new version.
ARG SIMPLYSIGN_BIN_URL=https://files.certum.eu/software/SimplySignDesktop/Linux-Ubuntu/2.9.14-9.4.3.0/SimplySignDesktop-2.9.14-9.4.3.0-x86_64-prod-ubuntu.bin
# renovate: datasource=maven depName=net.jsign:jsign
ARG JSIGN_VERSION=7.5

ENV DEBIAN_FRONTEND=noninteractive

# Headless X (Xvfb + fluxbox), UI automation (xdotool/wmctrl), PKCS#11 tooling,
# Java for jsign, and the system libraries SimplySign's bundled Qt loads.
RUN apt-get update && apt-get install -y --no-install-recommends \
      xvfb x11-utils xdotool wmctrl fluxbox \
      opensc opensc-pkcs11 default-jre-headless unzip openssl ca-certificates curl \
      libx11-6 libx11-xcb1 libxext6 libxcomposite1 libxi6 libice6 libsm6 libxrender1 \
      libxcb1 libxcb-glx0 libxau6 libpcsclite1 libglx-mesa0 libgl1 libdrm2 \
      libxkbcommon0 libxcb-xinerama0 libfontconfig1 libfreetype6 libdbus-1-3 procps \
      libglib2.0-0t64 libsqlite3-0 \
      libpulse-mainloop-glib0 libpulse0 libasound2t64 \
      libgstreamer1.0-0 libgstreamer-plugins-base1.0-0 \
      libwoff1 libharfbuzz0b libhyphen0 libwebp7 libwebpdemux2 \
      libnss3 libxtst6 libxss1 libxrandr2 libxdamage1 libxfixes3 libxcursor1 \
      libegl1 libgbm1 libxkbcommon-x11-0 \
    && rm -rf /var/lib/apt/lists/*

# SDK 10 next to the image's SDK 11.
RUN curl -fsSL -o /tmp/dotnet-install.sh https://dot.net/v1/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
    && rm /tmp/dotnet-install.sh \
    && ls -d /usr/share/dotnet/sdk/10.0.*

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

COPY --from=xml /opt/xml/lib/libxml2.so.2* /opt/xml/lib/libxslt.so.1* /opt/SimplySignDesktop/

# Fail the build when SimplySign or its X platform plugin would not find a shared library.
COPY <<'CS' /tmp/check-libraries.cs
using System.Diagnostics;

string[] binaries = ["/opt/SimplySignDesktop/SimplySignDesktop", "/opt/SimplySignDesktop/plugins/platforms/libqxcb.so"];

var missing = 0;
foreach (var binary in binaries)
{
    var ldd = new ProcessStartInfo("ldd", [binary]) { RedirectStandardOutput = true, RedirectStandardError = true };
    ldd.Environment["LD_LIBRARY_PATH"] = "/opt/SimplySignDesktop";

    foreach (var line in Process.RunAndCaptureText(ldd).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (line.Contains("not found", StringComparison.Ordinal))
        {
            Console.WriteLine($"{binary}: {line}");
            missing++;
        }
    }
}

return missing is 0 ? 0 : 1;
CS

RUN cd /tmp \
    && dotnet run check-libraries.cs \
    && rm -rf /tmp/check-libraries.cs /root/.local/share/dotnet/runfile

# jsign (signs .nupkg over PKCS#11).
RUN curl -fsSL -o /opt/jsign.jar "https://repo1.maven.org/maven2/net/jsign/jsign/${JSIGN_VERSION}/jsign-${JSIGN_VERSION}.jar"

# Contract consumed by the certum-sign action when it detects the prebuilt image:
# skip the apt/SimplySign/jsign install and use these baked-in paths.
#
# USER=root: SimplySignDesktop reads $USER at startup and segfaults (NULL deref
# in its bundled OpenSSL) when it is unset. Container jobs don't set $USER, so
# bake a default in — without it the login window never appears.
ENV CERTUM_PREBUILT=1 \
    USER=root \
    SS_DIST=/opt/SimplySignDesktop \
    JSIGN_JAR=/opt/jsign.jar \
    LD_LIBRARY_PATH=/opt/SimplySignDesktop

LABEL org.opencontainers.image.title="Certum SimplySign signer" \
      org.opencontainers.image.description="SimplySign Desktop + jsign + .NET 10 and 11 SDKs for headless NuGet signing on Linux" \
      org.opencontainers.image.source="https://github.com/reactiveui/actions-common"
