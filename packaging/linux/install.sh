#!/bin/sh
# CaptureIt — per-user install (no sudo needed)
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"

mkdir -p "$HOME/.local/bin" "$HOME/.local/share/applications" \
         "$HOME/.local/share/icons/hicolor/256x256/apps"
cp "$DIR/CaptureIt" "$HOME/.local/bin/captureit"
chmod +x "$HOME/.local/bin/captureit"
[ -f "$DIR/captureit.png" ] && cp "$DIR/captureit.png" \
    "$HOME/.local/share/icons/hicolor/256x256/apps/captureit.png"
sed "s|^Exec=.*|Exec=$HOME/.local/bin/captureit|" "$DIR/captureit.desktop" \
    > "$HOME/.local/share/applications/captureit.desktop"

echo "CaptureIt installed."
echo "  Run:  captureit   (or find 'CaptureIt' in your app menu)"
echo ""
echo "Screenshot backend: one of gnome-screenshot / spectacle / grim+slurp / scrot / ImageMagick is required."
echo "Clipboard support:  wl-clipboard (Wayland) or xclip (X11)."
