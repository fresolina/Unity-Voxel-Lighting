#!/usr/bin/env bash

set -eu

mkdir -p "$SITE_PATH"
rm -rf "$SITE_PATH/.git"
mkdir -p "$SITE_PATH/$PUBLISH_PATH"
mkdir -p "$SITE_PATH/versions"
mkdir -p "$SITE_PATH/previews"

if [ "$PUBLISH_KIND" = "preview" ] && [ -n "$PREVIEW_SLUG" ]; then
    find "$SITE_PATH/previews" -mindepth 1 -maxdepth 1 -type d -name "${PREVIEW_SLUG}-*" -exec rm -rf {} +
fi

rsync -a --delete "$PROJECT_PATH/build/WebGL/" "$SITE_PATH/$PUBLISH_PATH/"
if [ "$PUBLISH_KIND" = "release" ]; then
    rsync -a --delete "$PROJECT_PATH/build/WebGL/" "$SITE_PATH/latest/"
elif [ -n "$PREVIEW_LABEL" ]; then
    printf '%s\n' "$PREVIEW_LABEL" > "$SITE_PATH/$PUBLISH_PATH/.preview-label"
fi

bash .github/scripts/render-pages-index.sh
