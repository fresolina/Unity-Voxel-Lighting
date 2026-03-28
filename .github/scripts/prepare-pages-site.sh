#!/usr/bin/env bash

set -eu

html_escape() {
    sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g' -e 's/"/\&quot;/g' -e "s/'/\&#39;/g"
}

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
touch "$SITE_PATH/.nojekyll"

mapfile -t versions < <(find "$SITE_PATH/versions" -mindepth 1 -maxdepth 1 -type d -printf '%f\n' | sort -rV)
mapfile -t previews < <(find "$SITE_PATH/previews" -mindepth 1 -maxdepth 1 -type d -printf '%f\n' | sort)

{
    echo '<!doctype html>'
    echo '<html lang="en">'
    echo '<head>'
    echo '  <meta charset="utf-8">'
    echo '  <meta name="viewport" content="width=device-width, initial-scale=1">'
    echo '  <title>Lotec Voxel Lighting WebGL Builds</title>'
    echo '  <style>'
    echo '    :root { color-scheme: light; }'
    echo '    body { margin: 0; font-family: Georgia, "Times New Roman", serif; background: linear-gradient(180deg, #f6f1e8 0%, #e7eef5 100%); color: #1e2430; }'
    echo '    main { max-width: 760px; margin: 0 auto; padding: 48px 24px 80px; }'
    echo '    h1 { font-size: 2.5rem; margin: 0 0 12px; }'
    echo '    p { font-size: 1.05rem; line-height: 1.6; max-width: 60ch; }'
    echo '    .card { margin-top: 28px; padding: 24px; border-radius: 18px; background: rgba(255,255,255,0.72); box-shadow: 0 16px 40px rgba(39, 55, 77, 0.14); backdrop-filter: blur(10px); }'
    echo '    .latest { display: inline-block; margin: 8px 0 24px; padding: 12px 18px; border-radius: 999px; background: #1e2430; color: #fff; text-decoration: none; }'
    echo '    ul { list-style: none; padding: 0; margin: 0; }'
    echo '    li + li { margin-top: 12px; }'
    echo '    a.version-link { color: #0b5fff; text-decoration: none; font-weight: 700; }'
    echo '  </style>'
    echo '</head>'
    echo '<body>'
    echo '  <main>'
    echo '    <h1>Lotec Voxel Lighting</h1>'
    echo '    <p>Web builds for the Playground sample scene. Release builds are published from GitHub releases, and preview builds overwrite a stable per-branch URL while the link label shows the current build SHA.</p>'
    if [ -d "$SITE_PATH/latest" ]; then
        echo '    <a class="latest" href="latest/">Open latest release build</a>'
    fi
    echo '    <section class="card">'
    echo '      <h2>Released versions</h2>'
    echo '      <ul>'
    for version in "${versions[@]}"; do
        echo "        <li><a class=\"version-link\" href=\"versions/${version}/\">${version}</a></li>"
    done
    echo '      </ul>'
    echo '    </section>'
    echo '    <section class="card">'
    echo '      <h2>Preview builds</h2>'
    echo '      <ul>'
    for preview in "${previews[@]}"; do
        preview_label="$preview"
        if [ -f "$SITE_PATH/previews/$preview/.preview-label" ]; then
            preview_label="$(head -n 1 "$SITE_PATH/previews/$preview/.preview-label")"
        fi
        safe_preview_label="$(printf '%s' "$preview_label" | html_escape)"
        echo "        <li><a class=\"version-link\" href=\"previews/${preview}/\">${safe_preview_label}</a></li>"
    done
    echo '      </ul>'
    echo '    </section>'
    echo '  </main>'
    echo '</body>'
    echo '</html>'
} > "$SITE_PATH/index.html"
