#!/usr/bin/env bash

set -euo pipefail

command -v dotnet cargo >/dev/null || {
    echo "Hata: dotnet ve cargo PATH'te bulunmali."
    exit 1
}

CONFIGURATION="Debug"
TARGET_FRAMEWORK="net10.0"
RUST_PROFILE="debug"
DO_PUBLISH=false

usage() {
    echo "Kullanim: ./build.sh [--release] [--publish] [--help]"
    echo "  (bayraksiz)  Debug dev-loop"
    echo "  --release    Release dev-loop"
    echo "  --publish    Release paket (dist/migurdex-linux-x64.tar.gz)"
}

for arg in "$@"; do
    case "$arg" in
        --release) CONFIGURATION="Release"; RUST_PROFILE="release" ;;
        --publish|-p) DO_PUBLISH=true; CONFIGURATION="Release"; RUST_PROFILE="release" ;;
        --help|-h) usage; exit 0 ;;
        *) echo "Bilinmeyen arguman: $arg"; usage; exit 1 ;;
    esac
done

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RUST_DIR="$ROOT/Migurdex.Native"
API_DIR="$ROOT/Migurdex.Api"
OUT_DIR="$API_DIR/bin/$CONFIGURATION/$TARGET_FRAMEWORK"
OUT_PLUGINS="$OUT_DIR/Plugins"
SLN_PATH="$ROOT/Migurdex.slnx"

CYAN='\033[0;36m'
YELLOW='\033[1;33m'
GREEN='\033[0;32m'
GREY='\033[0;37m'
NC='\033[0m'

PLUGIN_PROJECTS=()
for _plugindir in "$ROOT"/Plugins/Migurdex.Plugins.*/; do
    _pluginname=$(basename "$_plugindir")
    if [ -f "$_plugindir$_pluginname.csproj" ]; then
        PLUGIN_PROJECTS+=("$_pluginname")
    fi
done
unset _plugindir _pluginname

copy_if_changed() {
    local src="$1"
    local dest="$2"

    if [ ! -f "$dest" ] || ! cmp -s "$src" "$dest"; then
        cp -f "$src" "$dest"
        return 0
    fi
    return 1
}

test_dotnet_restore_needed() {
    local project dir
    while IFS= read -r -d '' project; do
        dir=$(dirname "$project")
        if [ ! -f "$dir/obj/project.assets.json" ]; then
            return 0
        fi
    done < <(find "$ROOT" -name "*.csproj" -not -path "*/bin/*" -not -path "*/obj/*" -print0)
    return 1
}

if [ "$DO_PUBLISH" = true ]; then
    RUNTIME="linux-x64"
    DIST_DIR="$ROOT/dist"
    API_DIST_DIR="$DIST_DIR/api"

    echo -e "${CYAN}--- Migurdex Paketleme Başlatıldı (Release) ---${NC}"

    echo -e "${YELLOW}[1/6] Temizlik yapılıyor...${NC}"
    rm -rf "$DIST_DIR"
    mkdir -p "$API_DIST_DIR"

    echo -e "${YELLOW}[2/6] Rust Core derleniyor...${NC}"
    pushd "$RUST_DIR" > /dev/null
    cargo build --release
    popd > /dev/null

    echo -e "${YELLOW}[3/6] API derleniyor...${NC}"
    dotnet publish "$ROOT/Migurdex.Api/Migurdex.Api.csproj" \
        -c "$CONFIGURATION" \
        -r "$RUNTIME" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:PublishTrimmed=false \
        -o "$API_DIST_DIR"

    cp "$RUST_DIR/target/release/libmigurdex_native.so" "$API_DIST_DIR/"

    echo -e "${YELLOW}[4/6] CLI derleniyor...${NC}"
    dotnet publish "$ROOT/Migurdex.Cli/Migurdex.Cli.csproj" \
        -c "$CONFIGURATION" \
        -r "$RUNTIME" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:PublishTrimmed=true \
        -o "$DIST_DIR/temp_cli"

    mv "$DIST_DIR/temp_cli/migurdex" "$DIST_DIR/"
    rm -rf "$DIST_DIR/temp_cli"

    echo -e "${YELLOW}[5/6] Pluginler derleniyor...${NC}"
    mkdir -p "$API_DIST_DIR/Plugins"

    for pluginProject in "${PLUGIN_PROJECTS[@]}"; do
        echo -e "${GREY}Derleniyor: $pluginProject${NC}"
        dotnet publish "$ROOT/Plugins/$pluginProject/$pluginProject.csproj" \
            -c "$CONFIGURATION" \
            -o "$API_DIST_DIR/Plugins" \
            --no-self-contained
    done

    echo -e "${YELLOW}[6/6] Paket hazırlanıyor...${NC}"
    tar -czf "$DIST_DIR/migurdex-$RUNTIME.tar.gz" -C "$DIST_DIR" migurdex api
    echo -e "${GREEN}Arşiv oluşturuldu: $DIST_DIR/migurdex-$RUNTIME.tar.gz${NC}"
    exit 0
fi

echo -e "${CYAN}--- 1. Rust Core Kontrol Ediliyor... ---${NC}"
pushd "$RUST_DIR" > /dev/null

RUST_DLL="target/$RUST_PROFILE/libmigurdex_native.so"
SHOULD_BUILD_RUST=false

if [ ! -f "$RUST_DLL" ] || [ -n "$(find src -type f -newer "$RUST_DLL" -print -quit)" ]; then
    SHOULD_BUILD_RUST=true
fi

if [ "$SHOULD_BUILD_RUST" = true ]; then
    echo -e "${YELLOW}[RUST] Değişiklik algılandı, derleniyor...${NC}"
    if [ "$RUST_PROFILE" = "release" ]; then
        cargo build --release
    else
        cargo build
    fi
else
    echo -e "${GREEN}[RUST] Güncel, derleme atlanıyor.${NC}"
fi
popd > /dev/null


echo -e "${CYAN}--- 2. .NET Çözümü Derleniyor... ---${NC}"

if test_dotnet_restore_needed; then
    echo -e "${YELLOW}[DOTNET] Restore gerekli, paketler geri yükleniyor...${NC}"
    dotnet restore "$SLN_PATH"
else
    echo -e "${GREEN}[DOTNET] Restore atlanıyor.${NC}"
fi

dotnet build "$SLN_PATH" -c "$CONFIGURATION" -m --no-restore

echo -e "${CYAN}--- 3. Pluginler Hazırlanıyor... ---${NC}"

if [ ! -d "$OUT_DIR" ]; then
    mkdir -p "$OUT_DIR"
fi
mkdir -p "$OUT_PLUGINS"

EXPECTED_PLUGIN_FILES=$(mktemp)
for pluginProject in "${PLUGIN_PROJECTS[@]}"; do
    CLEAN_PROJECT=$(echo "$pluginProject" | tr -d '\r' | xargs)
    PLUGIN_BIN="$ROOT/Plugins/$CLEAN_PROJECT/bin/$CONFIGURATION/$TARGET_FRAMEWORK"
    if [ -d "$PLUGIN_BIN" ]; then
        for file in "$PLUGIN_BIN"/*; do
            if [ -f "$file" ]; then
                basename "$file" >> "$EXPECTED_PLUGIN_FILES"
            fi
        done
    fi
done

PRUNED_PLUGIN_FILES=0
if [ -s "$EXPECTED_PLUGIN_FILES" ]; then
    for dest in "$OUT_PLUGINS"/*; do
        if [ -f "$dest" ]; then
            if ! grep -qx "$(basename "$dest")" "$EXPECTED_PLUGIN_FILES"; then
                rm -f "$dest"
                PRUNED_PLUGIN_FILES=$((PRUNED_PLUGIN_FILES + 1))
            fi
        fi
    done
else
    echo -e "${YELLOW}[UYARI] Plugin bulunamadi, budama atlandi.${NC}"
fi
rm -f "$EXPECTED_PLUGIN_FILES"

if [ "$PRUNED_PLUGIN_FILES" -gt 0 ]; then
    echo -e "${YELLOW}[PLUGIN] $PRUNED_PLUGIN_FILES eski dosya silindi.${NC}"
fi

COPIED_PLUGIN_FILES=0

for pluginProject in "${PLUGIN_PROJECTS[@]}"; do
    CLEAN_PROJECT=$(echo "$pluginProject" | tr -d '\r' | xargs)
    PLUGIN_BIN="$ROOT/Plugins/$CLEAN_PROJECT/bin/$CONFIGURATION/$TARGET_FRAMEWORK"

    if [ -d "$PLUGIN_BIN" ]; then
        shopt -s nullglob
        for file in "$PLUGIN_BIN"/*; do
            if [ -f "$file" ]; then
                filename=$(basename "$file")
                DESTINATION="$OUT_PLUGINS/$filename"
                if copy_if_changed "$file" "$DESTINATION"; then
                    COPIED_PLUGIN_FILES=$((COPIED_PLUGIN_FILES + 1))
                fi
            fi
        done
        shopt -u nullglob
    fi
done

echo -e "${GREEN}[PLUGIN] $COPIED_PLUGIN_FILES dosya güncellendi.${NC}"

echo -e "${CYAN}--- 4. Kitaplıklar Kopyalanıyor... ---${NC}"
RUST_SO="$RUST_DIR/target/$RUST_PROFILE/libmigurdex_native.so"

if [ -f "$RUST_SO" ]; then
    DESTINATION_RUST_SO="$OUT_DIR/libmigurdex_native.so"
    if copy_if_changed "$RUST_SO" "$DESTINATION_RUST_SO"; then
        echo -e "${GREEN}[RUST] libmigurdex_native.so kopyalandı.${NC}"
    else
        echo -e "${GREEN}[RUST] libmigurdex_native.so güncel, kopyalama atlandı.${NC}"
    fi
else
    echo -e "${YELLOW}[UYARI] libmigurdex_native.so bulunamadı!${NC}"
fi

echo -e "\n${GREEN}[BAŞARILI] Tamamlandı!${NC}"
