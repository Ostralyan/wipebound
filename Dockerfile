# The dedicated server, which is the only thing allowed to submit a ranked run.
#
# Build the binary first -- this image only packages it:
#   godot --headless --path . --export-release "Linux Server" build/linux/wipebound-server.x86_64
#   docker build -t wipebound-server .
#
# The export embeds the pck and the .NET assemblies, so the whole game is one
# file and this image is a base plus that file.
FROM ubuntu:24.04

# libicu is .NET's globalization data. Without it the runtime refuses to start
# unless invariant mode is forced, and forcing that would quietly change how
# numbers format -- which is exactly what the content fingerprint depends on.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libicu74 ca-certificates \
 && rm -rf /var/lib/apt/lists/*

RUN useradd --system --create-home --uid 10001 wipebound

COPY --chown=root:root build/linux/wipebound-server.x86_64 /usr/local/bin/wipebound-server
RUN chmod 0755 /usr/local/bin/wipebound-server

USER wipebound
WORKDIR /home/wipebound

# ENet is UDP.
EXPOSE 7777/udp

# WIPEBOUND_BACKEND_URL, WIPEBOUND_SERVER_TOKEN and WIPEBOUND_SERVER_ID are read
# at startup. Without them the server plays fine and simply logs runs instead of
# submitting them, which is the right behaviour for a scrim box.
ENTRYPOINT ["/usr/local/bin/wipebound-server", "--headless", "--"]
CMD ["--server", "--port", "7777"]
