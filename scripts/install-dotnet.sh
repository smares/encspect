#!/bin/bash

# Installs the .NET 10 SDK in cloud sessions. Always exits 0: a failed download must not block
# the session from starting, and the message printed here tells Claude what went wrong.

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

INSTALL_DIR=/usr/share/dotnet

if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  {
    echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1'
    echo 'export DOTNET_NOLOGO=1'
  } >> "$CLAUDE_ENV_FILE"
fi

if dotnet --version 2>/dev/null | grep -q '^10\.'; then
  echo "The .NET SDK $(dotnet --version) is already installed."
  exit 0
fi

installer="$(mktemp)"
if ! curl -fsSL --retry 3 -o "$installer" https://dot.net/v1/dotnet-install.sh; then
  echo "The .NET SDK install FAILED: dotnet-install.sh could not be downloaded from dot.net."
  echo "The environment's network access probably blocks it. Ask the user to allow dot.net,"
  echo "builds.dotnet.microsoft.com, dotnetcli.azureedge.net, api.nuget.org and globalcdn.nuget.org."
  exit 0
fi

if bash "$installer" --channel 10.0 --install-dir "$INSTALL_DIR" > /tmp/dotnet-install.log 2>&1; then
  ln -sf "$INSTALL_DIR/dotnet" /usr/local/bin/dotnet
  echo "Installed the .NET SDK $(dotnet --version)."
else
  echo "The .NET SDK install FAILED. A 403 in the log below means a blocked host: ask the user to"
  echo "allow it in the environment's network access. Last lines of the install log:"
  tail -n 15 /tmp/dotnet-install.log
fi

exit 0
