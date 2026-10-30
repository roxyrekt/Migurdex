#!/bin/sh

self=$0
if command -v readlink >/dev/null 2>&1; then
    resolved=$(readlink -f "$0" 2>/dev/null) && [ -n "$resolved" ] && self=$resolved
fi
dir=$(CDPATH= cd -- "$(dirname -- "$self")" && pwd)
exec "$dir/cli/migurdex" "$@"
