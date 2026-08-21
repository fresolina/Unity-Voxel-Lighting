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
# Addressables resolves the relative Remote.BuildPath ("ServerData/[BuildTarget]") against the
# CURRENT DIRECTORY, which is the project folder in the editor but the repository root under game-ci,
# where Unity is launched from the workspace. Check both rather than pin one.
resolve_remote_content_path() {
    local candidate

    for candidate in \
        "${REMOTE_CONTENT_PATH:-}" \
        "$PROJECT_PATH/ServerData/WebGL" \
        "ServerData/WebGL"
    do
        if [ -n "$candidate" ] && [ -d "$candidate" ]; then
            printf '%s\n' "$candidate"
            return 0
        fi
    done

    echo "No packed remote content found." >&2
    echo "Checked: '$PROJECT_PATH/ServerData/WebGL' and 'ServerData/WebGL'." >&2
    echo "Every level is an Addressable on the Remote path, so the player would load nothing at" >&2
    echo "all - refusing to publish it. ServerData directories present in the tree:" >&2
    find . -maxdepth 4 -type d -name 'ServerData' -not -path './.git/*' >&2 || true
    return 1
}

REMOTE_CONTENT_PATH="$(resolve_remote_content_path)"
mkdir -p "$SITE_PATH/$PUBLISH_PATH/ServerData/WebGL"
rsync -a --delete "$REMOTE_CONTENT_PATH/" "$SITE_PATH/$PUBLISH_PATH/ServerData/WebGL/"

bash .github/scripts/render-pages-index.sh
