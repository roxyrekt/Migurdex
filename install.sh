#!/usr/bin/env bash

set -euo pipefail

REPO="roxyrekt/Migurdex"
GITHUB_URL="https://github.com/$REPO"
RAW_URL="https://raw.githubusercontent.com/$REPO/main"

if [ -t 1 ]; then
    CYAN='\033[0;36m'
    GREEN='\033[0;32m'
    YELLOW='\033[1;33m'
    RED='\033[0;31m'
    GREY='\033[0;37m'
    BOLD='\033[1m'
    NC='\033[0m'
else
    CYAN=''
    GREEN=''
    YELLOW=''
    RED=''
    GREY=''
    BOLD=''
    NC=''
fi

INSTALL_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/migurdex"
BIN_DIR="${XDG_BIN_HOME:-$HOME/.local/bin}"
CONFIG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/migurdex"
DESKTOP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICON_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/128x128/apps"
PIXMAPS_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/pixmaps"

uninstall() {
    local purge="${1:-false}"

    echo -e "${YELLOW}Migurdex kaldırılıyor...${NC}"
    
    rm -rf "$INSTALL_DIR"
    rm -f "$BIN_DIR/migurdex"
    rm -f "$DESKTOP_DIR/migurdex.desktop"
    rm -f "$ICON_DIR/migurdex.png"
    rm -f "$PIXMAPS_DIR/migurdex.png"

    if command -v update-desktop-database >/dev/null 2>&1; then
        update-desktop-database "$DESKTOP_DIR" >/dev/null 2>&1 || true
    fi

    if [ "$purge" = "true" ]; then
        rm -rf "$CONFIG_DIR"
        echo -e "${GREEN}✓ Migurdex ve tüm yapılandırma/geçmiş verileri tamamen temizlendi.${NC}"
    else
        echo -e "${GREEN}✓ Migurdex başarıyla kaldırıldı.${NC}"
        if [ -d "$CONFIG_DIR" ]; then
            echo -e "${CYAN}Not: Yapılandırma ve geçmiş dosyalarınız saklandı ($CONFIG_DIR).${NC}"
            echo -e "${GREY}Tüm kalıntıları da silmek isterseniz: curl -fsSL $RAW_URL/install.sh | bash -s -- --purge${NC}"
        fi
    fi
    exit 0
}

for arg in "$@"; do
    case "$arg" in
        --purge|-p)
            uninstall true
            ;;
        --uninstall|-u)
            uninstall false
            ;;
        --help|-h)
            echo "Migurdex Kurulum Scripti"
            echo "Kullanım: ./install.sh [seçenekler]"
            echo "  --uninstall, -u   Migurdex'i sistemden kaldırır (ayarları korur)"
            echo "  --purge, -p       Migurdex'i ve tüm yapılandırma/geçmiş dosyalarını tamamen siler"
            echo "  --help, -h        Bu yardım mesajını gösterir"
            exit 0
            ;;
        *)
            echo -e "${YELLOW}Bilinmeyen parametre: $arg (yoksayılıyor)${NC}"
            ;;
    esac
done

echo -e "${CYAN}${BOLD}"
echo "    __  ____                        __          "
echo "   /  |/  (_)___ ___  ___________  / /__  _  __ "
echo "  / /|_/ / / __ \`/ / / / ___/ __ \\/ _ \\ |/ /  "
echo " / /  / / / /_/ / /_/ / /  / /_/ /  __/>  <   "
echo "/_/  /_/_/\\__, /\\__,_/_/  /_____/\\___/_/|_|   "
echo "         /____/                                "
echo -e "${NC}"
echo -e "${CYAN}--- Migurdex Linux Kurulumu Başlatılıyor ---${NC}\n"

OS="$(uname -s)"
ARCH="$(uname -m)"

if [ "$OS" != "Linux" ]; then
    echo -e "${RED}Hata: Bu script sadece Linux sistemleri desteklemektedir ($OS algılandı).${NC}"
    exit 1
fi

if [ "$ARCH" != "x86_64" ] && [ "$ARCH" != "amd64" ]; then
    echo -e "${RED}Hata: Şu anda yalnızca x86_64 mimarisi desteklenmektedir ($ARCH algılandı).${NC}"
    exit 1
fi

for cmd in curl tar; do
    if ! command -v "$cmd" >/dev/null 2>&1; then
        echo -e "${RED}Hata: Gerekli '$cmd' komutu bulunamadı. Lütfen yükleyin.${NC}"
        exit 1
    fi
done

echo -e "${YELLOW}[1/5] Son sürüm kontrol ediliyor...${NC}"
LATEST_TAG=""
if API_RESP=$(curl -fsSL -H "Accept: application/vnd.github.v3+json" "https://api.github.com/repos/$REPO/releases/latest" 2>/dev/null); then
    LATEST_TAG=$(echo "$API_RESP" | grep '"tag_name":' | head -n 1 | sed -E 's/.*"tag_name":\s*"([^"]+)".*/\1/')
fi

if [ -n "$LATEST_TAG" ]; then
    DOWNLOAD_URL="https://github.com/$REPO/releases/download/$LATEST_TAG/migurdex-linux-x64.tar.gz"
    echo -e "${GREEN}Bulunan son sürüm: ${BOLD}$LATEST_TAG${NC}"
else
    DOWNLOAD_URL="https://github.com/$REPO/releases/latest/download/migurdex-linux-x64.tar.gz"
    echo -e "${YELLOW}Son sürüm doğrudan 'latest' kanalından indirilecek.${NC}"
fi

TMP_DIR=$(mktemp -d)
cleanup() {
    rm -rf "$TMP_DIR"
}
trap cleanup EXIT

echo -e "${YELLOW}[2/5] Migurdex paketi indiriliyor...${NC}"
ARCHIVE_PATH="$TMP_DIR/migurdex.tar.gz"

if ! curl -fSL --progress-bar "$DOWNLOAD_URL" -o "$ARCHIVE_PATH"; then
    echo -e "${RED}Hata: Paket indirilemedi! Lütfen internet bağlantınızı veya GitHub release durumunu kontrol edin.${NC}"
    exit 1
fi

echo -e "${YELLOW}[3/5] Dosyalar kuruluyor (~/.local/share/migurdex)...${NC}"
mkdir -p "$INSTALL_DIR"
mkdir -p "$BIN_DIR"

rm -rf "$INSTALL_DIR/api" "$INSTALL_DIR/migurdex"

tar -xzf "$ARCHIVE_PATH" -C "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/migurdex"
if [ -f "$INSTALL_DIR/api/Migurdex.Api" ]; then
    chmod +x "$INSTALL_DIR/api/Migurdex.Api"
fi

ln -sf "$INSTALL_DIR/migurdex" "$BIN_DIR/migurdex"

echo -e "${YELLOW}[4/5] Masaüstü entegrasyonu ayarlanıyor...${NC}"
mkdir -p "$DESKTOP_DIR" "$ICON_DIR" "$PIXMAPS_DIR"

curl -fsSL "$RAW_URL/assets/packaging/migurdex.png" -o "$ICON_DIR/migurdex.png" 2>/dev/null || true
cp -f "$ICON_DIR/migurdex.png" "$PIXMAPS_DIR/migurdex.png" 2>/dev/null || true

cat > "$DESKTOP_DIR/migurdex.desktop" << EOF
[Desktop Entry]
Name=Migurdex
Comment=Terminalden anime arama ve izleme aracı
Exec=$BIN_DIR/migurdex
Icon=migurdex
Type=Application
Categories=AudioVideo;Video;Player;
Terminal=true
StartupNotify=false
EOF
chmod +x "$DESKTOP_DIR/migurdex.desktop"

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$DESKTOP_DIR" >/dev/null 2>&1 || true
fi

echo -e "${YELLOW}[5/5] Sistem kontrolleri yapılıyor...${NC}"

if ! command -v mpv >/dev/null 2>&1; then
    echo -e "\n${YELLOW}⚠ UYARI: Sisteminizde 'mpv' video oynatıcısı bulunamadı!${NC}"
    echo -e "Migurdex videoları oynatmak için MPV'ye ihtiyaç duyar. Yüklemek için:"
    if command -v pacman >/dev/null 2>&1; then
        echo -e "  ${BOLD}sudo pacman -S mpv${NC}"
    elif command -v apt >/dev/null 2>&1; then
        echo -e "  ${BOLD}sudo apt update && sudo apt install mpv${NC}"
    elif command -v dnf >/dev/null 2>&1; then
        echo -e "  ${BOLD}sudo dnf install mpv${NC}"
    elif command -v zypper >/dev/null 2>&1; then
        echo -e "  ${BOLD}sudo zypper install mpv${NC}"
    else
        echo -e "  Paket yöneticinizden 'mpv' paketini kurun."
    fi
else
    echo -e "${GREEN}✓ MPV oynatıcısı algılandı.${NC}"
fi

PATH_WARNING=false
case ":$PATH:" in
    *":$BIN_DIR:"*) ;;
    *) PATH_WARNING=true ;;
esac

echo -e "\n${GREEN}${BOLD}🎉 Migurdex kurulumu başarıyla tamamlandı!${NC}\n"

if [ "$PATH_WARNING" = true ]; then
    echo -e "${YELLOW}Not: '$BIN_DIR' dizini mevcut PATH değişkeninizde bulunmuyor.${NC}"
    echo -e "Komut satırından doğrudan 'migurdex' yazarak çalıştırabilmek için kabuk konfigürasyonunuza şu satırı ekleyin:"
    
    CURRENT_SHELL="$(basename "${SHELL:-bash}")"
    if [ "$CURRENT_SHELL" = "zsh" ]; then
        echo -e "  ${BOLD}echo 'export PATH=\"\$HOME/.local/bin:\$PATH\"' >> ~/.zshrc && source ~/.zshrc${NC}"
    elif [ "$CURRENT_SHELL" = "fish" ]; then
        echo -e "  ${BOLD}fish_add_path ~/.local/bin${NC}"
    else
        echo -e "  ${BOLD}echo 'export PATH=\"\$HOME/.local/bin:\$PATH\"' >> ~/.bashrc && source ~/.bashrc${NC}"
    fi
    echo ""
fi

echo -e "Çalıştırmak için: ${CYAN}${BOLD}migurdex${NC}"
echo -e "Kaldırmak için:   ${GREY}curl -fsSL $RAW_URL/install.sh | bash -s -- --uninstall${NC}"
echo -e "Tam temizlik için:${GREY}curl -fsSL $RAW_URL/install.sh | bash -s -- --purge${NC}\n"
