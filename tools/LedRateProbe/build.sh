#!/usr/bin/env bash
# LedRateProbe 构建 (本机 reg.exe 被安全策略拉黑 → vcvars64.bat 失败 → 手工拼 MSVC/SDK 环境直接调 cl)
set -e
export PATH="/usr/bin:/bin:/c/Windows/System32:/c/Windows:$PATH"

VC='C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231'
KIT='C:\Program Files (x86)\Windows Kits\10'
SDKVER='10.0.26100.0'

export INCLUDE="$VC\\include;$KIT\\Include\\$SDKVER\\ucrt;$KIT\\Include\\$SDKVER\\um;$KIT\\Include\\$SDKVER\\shared;$KIT\\Include\\$SDKVER\\winrt;$KIT\\Include\\$SDKVER\\cppwinrt"
export LIB="$VC\\lib\\x64;$KIT\\Lib\\$SDKVER\\ucrt\\x64;$KIT\\Lib\\$SDKVER\\um\\x64"
export PATH="/c/Program Files/Microsoft Visual Studio/18/Community/VC/Tools/MSVC/14.51.36231/bin/Hostx64/x64:$PATH"

cd "$(dirname "$0")"
mkdir -p obj

MSYS2_ARG_CONV_EXCL='*' cl.exe /nologo /EHsc /O2 /W3 /std:c++17 /utf-8 \
  /DNOMINMAX /D_UNICODE /DUNICODE \
  /I'..\..\native\AmbientCore' \
  main.cpp \
  '../../native/AmbientCore/serial.cpp' \
  /Fe:led_rate_probe.exe /Fo:obj/

echo "[probe] OK -> $(ls -l led_rate_probe.exe | awk '{print $5, $9}')"
