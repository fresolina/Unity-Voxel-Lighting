#!/usr/bin/env bash

set -eu

resolve_build_output_path() {
    local candidate
    local nested_candidate

    for candidate in \
        "${BUILD_OUTPUT_PATH:-}" \
        "$PROJECT_PATH/build/WebGL" \
        "build/WebGL"
    do
        if [ -n "$candidate" ] && [ -d "$candidate" ]; then
            if [ -f "$candidate/index.html" ]; then
                printf '%s\n' "$candidate"
                return 0
            fi

            nested_candidate="$candidate/WebGL"
            if [ -f "$nested_candidate/index.html" ]; then
                printf '%s\n' "$nested_candidate"
                return 0
            fi
        fi
    done

    echo "Could not find a WebGL build output directory." >&2
    echo "Checked: '$PROJECT_PATH/build/WebGL', '$PROJECT_PATH/build/WebGL/WebGL', 'build/WebGL', and 'build/WebGL/WebGL'." >&2
    return 1
}

BUILD_OUTPUT_PATH="$(resolve_build_output_path)"

mkdir -p "$SITE_PATH"
rm -rf "$SITE_PATH/.git"
mkdir -p "$SITE_PATH/$PUBLISH_PATH"
mkdir -p "$SITE_PATH/versions"
mkdir -p "$SITE_PATH/previews"

if [ "$PUBLISH_KIND" = "preview" ] && [ -n "$PREVIEW_SLUG" ]; then
    find "$SITE_PATH/previews" -mindepth 1 -maxdepth 1 -type d -name "${PREVIEW_SLUG}-*" -exec rm -rf {} +
fi

rsync -a --delete "$BUILD_OUTPUT_PATH/" "$SITE_PATH/$PUBLISH_PATH/"
if [ "$PUBLISH_KIND" = "release" ]; then
    rsync -a --delete "$BUILD_OUTPUT_PATH/" "$SITE_PATH/latest/"
elif [ -n "$PREVIEW_LABEL" ]; then
    printf '%s\n' "$PREVIEW_LABEL" > "$SITE_PATH/$PUBLISH_PATH/.preview-label"
fi

# The levels are NOT in the player: Playground and Sponza are Addressable groups on the Remote path,
# fetched at runtime (Build Settings ships Bootstrap alone). Publish the content this build was packed
# against right next to the build, so a preview and a release never share - and so never silently
# overwrite - each other's levels. RemoteContentBuild pointed the player's Remote.LoadPath here.
REMOTE_CONTENT_PATH="$PROJECT_PATH/ServerData/WebGL"
if [ ! -d "$REMOTE_CONTENT_PATH" ]; then
    echo "No packed remote content at '$REMOTE_CONTENT_PATH'." >&2
    echo "The player would load no levels at all, so refuse to publish it." >&2
    exit 1
fi
mkdir -p "$SITE_PATH/$PUBLISH_PATH/ServerData/WebGL"
rsync -a --delete "$REMOTE_CONTENT_PATH/" "$SITE_PATH/$PUBLISH_PATH/ServerData/WebGL/"

bash .github/scripts/render-pages-index.sh
