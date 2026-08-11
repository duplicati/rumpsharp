#!/bin/bash
# Builds, packages and ad-hoc signs the Avalonia sample as a real .app bundle.
#
# This is the shape a menu-bar app actually ships in, and it exercises RumpSharp's *other* code path:
# the process has its own bundle identity, so notifications go straight to UNUserNotificationCenter
# in-process and no helper is started. Run the sample from bin/ instead and you get the helper path.
#
#   ./package.sh              # package only
#   ./package.sh --run        # package, then run it with the console attached
#   ./package.sh --self-contained
#
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
project="$here/RumpSharp.TrayIcon.Sample.csproj"

name="RumpSharp Tray"
identifier="dev.rumpsharp.trayicon"
executable="RumpSharp.TrayIcon.Sample"
version="1.0.0"

app="$repo/artifacts/$name.app"
contents="$app/Contents"

run=false
self_contained=false
for argument in "$@"; do
    case "$argument" in
        --run|-r) run=true ;;
        --self-contained) self_contained=true ;;
        *) echo "unknown option: $argument" >&2; exit 2 ;;
    esac
done

case "$(uname -m)" in
    arm64) rid="osx-arm64" ;;
    x86_64) rid="osx-x64" ;;
    *) echo "unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

# ------------------------------------------------------------------ build

echo "==> publishing $executable ($rid, self-contained=$self_contained)"
publish="$here/bin/package/$rid"
rm -rf "$publish"
dotnet publish "$project" \
    --configuration Release \
    --runtime "$rid" \
    --self-contained "$self_contained" \
    --output "$publish" \
    --nologo \
    --verbosity quiet

# ------------------------------------------------------------------ assemble

echo "==> assembling $app"
rm -rf "$app"
mkdir -p "$contents/MacOS" "$contents/Resources"

cp -R "$publish/." "$contents/MacOS/"

# codesign treats debug symbols as unsigned nested code and refuses to seal the bundle.
find "$contents/MacOS" -name '*.pdb' -delete
find "$contents/MacOS" -name '*.dSYM' -prune -exec rm -rf {} +

chmod +x "$contents/MacOS/$executable"

# The sample draws its own artwork on first run and hands the PNG to AppBundleOptions - which does
# nothing once the app is bundled, because then the icon has to be in the bundle. So take the same
# PNG and build the .icns here. Starting the freshly built app is what produces it.
assets="${TMPDIR:-/tmp}/rumpsharp-tray-sample"
png="$assets/app-icon.png"
if [[ ! -f "$png" ]]; then
    echo "==> generating icon artwork (briefly starting the app)"
    "$contents/MacOS/$executable" >/dev/null 2>&1 &
    starter=$!
    for _ in $(seq 1 100); do
        [[ -f "$png" ]] && break
        sleep 0.1
    done

    kill "$starter" 2>/dev/null || true
    wait "$starter" 2>/dev/null || true
fi

icon=""
if [[ -f "$png" ]]; then
    echo "==> converting $png to AppIcon.icns"
    iconset="$(mktemp -d)/AppIcon.iconset"
    mkdir -p "$iconset"
    for size in 16 32 128 256 512; do
        sips -z "$size" "$size" "$png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
        sips -z "$((size * 2))" "$((size * 2))" "$png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
    done

    iconutil -c icns "$iconset" -o "$contents/Resources/AppIcon.icns"
    rm -rf "$(dirname "$iconset")"
    icon="  <key>CFBundleIconFile</key>
  <string>AppIcon.icns</string>"
else
    echo "==> no artwork found; the bundle will use the default icon"
fi

cat > "$contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>$name</string>
  <key>CFBundleDisplayName</key>
  <string>$name</string>
  <key>CFBundleIdentifier</key>
  <string>$identifier</string>
  <key>CFBundleExecutable</key>
  <string>$executable</string>
  <key>CFBundleVersion</key>
  <string>$version</string>
  <key>CFBundleShortVersionString</key>
  <string>$version</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>NSPrincipalClass</key>
  <string>NSApplication</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>LSMinimumSystemVersion</key>
  <string>11.0</string>
  <!-- Menu-bar only: no Dock icon, no app switcher entry. The equivalent of ShowInDock = false. -->
  <key>LSUIElement</key>
  <true/>
$icon
</dict>
</plist>
PLIST

# ------------------------------------------------------------------ sign

echo "==> signing"
# Ad-hoc, like AppBundle does. --deep because a .NET publish directory is full of nested Mach-O
# libraries which all have to be signed for the outer signature to validate.
codesign --force --deep --sign - --timestamp=none "$app"
codesign --verify --deep --strict "$app"
echo "    signature verified"

# ------------------------------------------------------------------ report

echo
echo "packaged: $app"
echo "size    : $(du -sh "$app" | cut -f1)"
echo
echo "Run it with the console attached (this still gives the process its bundle identity):"
echo "  \"$contents/MacOS/$executable\""
echo
echo "Or hand it to LaunchServices, which detaches stdout/stderr:"
echo "  open \"$app\""
echo
echo "Expect 'in-process: True' and no rumpsharp-helper child process. A bundle for the same"
echo "identifier may be left over from unbundled runs; it is unused now and safe to delete:"
echo "  rm -rf \"\$HOME/Library/Application Support/RumpSharp/$name.app\""

if [[ "$run" == true ]]; then
    echo
    echo "==> running (Ctrl+C to stop)"
    exec "$contents/MacOS/$executable"
fi
