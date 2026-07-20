#!/bin/sh
# CaptureIt — per-user install (no sudo needed)
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"

mkdir -p "$HOME/.local/bin" "$HOME/.local/share/applications"
cp "$DIR/CaptureIt" "$HOME/.local/bin/captureit"
chmod +x "$HOME/.local/bin/captureit"
sed "s|^Exec=.*|Exec=$HOME/.local/bin/captureit|" "$DIR/captureit.desktop" \
    > "$HOME/.local/share/applications/captureit.desktop"

echo "CaptureIt installed."
echo "  Run:  captureit   (or find 'CaptureIt' in your app menu)"
echo ""
echo "Screenshot backend: one of gnome-screenshot / spectacle / grim+slurp / scrot / ImageMagick is required."
echo "Clipboard support:  wl-clipboard (Wayland) or xclip (X11)."
