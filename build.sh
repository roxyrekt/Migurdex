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

dedupe_hardlinks() {
    local cli_dir="$1"
    local api_dir="$2"

    if [ ! -d "$cli_dir" ] || [ ! -d "$api_dir" ]; then
        return 0
    fi

    local linked=0 saved=0
    local rel src dst size

    while IFS= read -r -d '' src; do
        rel="${src#$cli_dir/}"
        dst="$api_dir/$rel"
        [ -f "$dst" ] || continue

        [ "$(stat -c %s "$src")" = "$(stat -c %s "$dst")" ] || continue
        [ "$(sha256sum "$src" | cut -d' ' -f1)" = "$(sha256sum "$dst" | cut -d' ' -f1)" ] || continue

        size=$(stat -c %s "$src")
        if ln "$dst" "$src.hltmp" 2>/dev/null; then
            mv -f "$src.hltmp" "$src" 2>/dev/null
            rm -f "$src.hltmp" 2>/dev/null
            linked=$((linked + 1))
            saved=$((saved + size))
        else
            rm -f "$src.hltmp"
        fi
    done < <(find "$cli_dir" -type f -print0)

    if [ "$linked" -gt 0 ]; then
        echo -e "${GREY}  [HARDLINK] $linked dosya paylasildi, $((saved / 1024 / 1024)) MB disk tasarrufu.${NC}"
    fi
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
    case "$(uname -m)" in
        aarch64|arm64) RUNTIME="linux-arm64" ;;
        *) RUNTIME="linux-x64" ;;
    esac
    DIST_DIR="$ROOT/dist"
    CLI_DIST_DIR="$DIST_DIR/cli"
    API_DIST_DIR="$DIST_DIR/api"

    echo -e "${CYAN}--- Migurdex Paketleme Başlatıldı (Release) ---${NC}"

    echo -e "${YELLOW}[1/6] Temizlik yapılıyor...${NC}"
    rm -rf "$DIST_DIR"
    mkdir -p "$CLI_DIST_DIR"
    mkdir -p "$API_DIST_DIR"

    echo -e "${YELLOW}[2/6] Rust Core derleniyor...${NC}"
    pushd "$RUST_DIR" > /dev/null
    cargo build --release --locked
    popd > /dev/null

    echo -e "${YELLOW}[3/6] API derleniyor...${NC}"
    dotnet publish "$ROOT/Migurdex.Api/Migurdex.Api.csproj" \
        -c "$CONFIGURATION" \
        -r "$RUNTIME" \
        --self-contained true \
        -p:PublishSingleFile=false \
        -p:PublishTrimmed=false \
        -o "$API_DIST_DIR"

    cp "$RUST_DIR/target/release/libmigurdex_native.so" "$API_DIST_DIR/"

    echo -e "${YELLOW}[4/6] CLI derleniyor...${NC}"
    dotnet publish "$ROOT/Migurdex.Cli/Migurdex.Cli.csproj" \
        -c "$CONFIGURATION" \
        -r "$RUNTIME" \
        --self-contained true \
        -p:PublishSingleFile=false \
        -p:PublishTrimmed=false \
        -o "$CLI_DIST_DIR"

    echo -e "${YELLOW}[5/6] Pluginler derleniyor...${NC}"
    mkdir -p "$API_DIST_DIR/Plugins"

    for pluginProject in "${PLUGIN_PROJECTS[@]}"; do
        dotnet publish "$ROOT/Plugins/$pluginProject/$pluginProject.csproj" \
            -c "$CONFIGURATION" \
            -o "$API_DIST_DIR/Plugins" \
            --no-self-contained
    done

    echo -e "${YELLOW}[6/6] Paket hazırlanıyor...${NC}"
    dedupe_hardlinks "$CLI_DIST_DIR" "$API_DIST_DIR"

    cp "$ROOT/assets/packaging/migurdex-launcher.sh" "$DIST_DIR/migurdex"
    chmod +x "$DIST_DIR/migurdex"

    echo -e "${GREY}  payload: $(du -sh --apparent-size "$DIST_DIR" | cut -f1) gorunur, "\
"$(du -sh "$DIST_DIR" | cut -f1) diskte${NC}"

    tar -cf - -C "$DIST_DIR" \
        --exclude="migurdex-$RUNTIME.tar.xz" \
        --exclude="migurdex-$RUNTIME.tar.gz" \
        . | xz -9 -T0 > "$ROOT/migurdex-$RUNTIME.tar.xz"
    mv "$ROOT/migurdex-$RUNTIME.tar.xz" "$DIST_DIR/"

    tar -czf "$ROOT/migurdex-$RUNTIME.tar.gz" -C "$DIST_DIR" \
        --exclude="migurdex-$RUNTIME.tar.xz" \
        --exclude="migurdex-$RUNTIME.tar.gz" \
        .
    mv "$ROOT/migurdex-$RUNTIME.tar.gz" "$DIST_DIR/"

    XZ_ARCHIVE="$DIST_DIR/migurdex-$RUNTIME.tar.xz"
    GZ_ARCHIVE="$DIST_DIR/migurdex-$RUNTIME.tar.gz"
    hl_count=$(tar -tvJf "$XZ_ARCHIVE" | grep -c '^h' || true)

    echo -e "${YELLOW}[7/7] Delta blob deposu hazırlanıyor...${NC}"
    python3 "$ROOT/assets/packaging/build-blobs.py" \
        --input "$DIST_DIR" \
        --output-dir "$DIST_DIR" \
        --runtime "$RUNTIME" \
        --exclude '*.tar.xz' \
        --exclude '*.tar.gz' \
        --exclude '*.zip' \
        --exclude '*.blobs.tar' \
        --exclude '*.manifest.json.gz'

    BLOBS_TAR="$DIST_DIR/migurdex-$RUNTIME.blobs.tar"
    MANIFEST="$DIST_DIR/migurdex-$RUNTIME.manifest.json.gz"

    echo -e "${GREEN}Arşivler oluşturuldu:${NC}"
    printf "  %-40s %6.1f MB  (hardlink: %s)\n" \
        "migurdex-$RUNTIME.tar.xz" \
        "$(awk -v s="$(stat -c %s "$XZ_ARCHIVE")" 'BEGIN{print s/1048576}')" \
        "$hl_count"
    printf "  %-40s %6.1f MB  (eski istemciler)\n" \
        "migurdex-$RUNTIME.tar.gz" \
        "$(awk -v s="$(stat -c %s "$GZ_ARCHIVE")" 'BEGIN{print s/1048576}')"
    printf "  %-40s %6.1f MB  (delta)\n" \
        "migurdex-$RUNTIME.blobs.tar" \
        "$(awk -v s="$(stat -c %s "$BLOBS_TAR")" 'BEGIN{print s/1048576}')"
    printf "  %-40s %6.1f KB  (delta manifest)\n" \
        "migurdex-$RUNTIME.manifest.json.gz" \
        "$(awk -v s="$(stat -c %s "$MANIFEST")" 'BEGIN{print s/1024}')"

    if [ "${hl_count:-0}" -lt 100 ]; then
        echo -e "${YELLOW}[UYARI] Beklenen ~204 hardlink, bulunan: $hl_count${NC}"
    fi
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
        cargo build --release --locked
    else
        cargo build --locked
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
