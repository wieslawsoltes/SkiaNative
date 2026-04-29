#!/usr/bin/env bash
set -euo pipefail

PR_NUMBER="${SKIASHARP_PR3779_PR:-3779}"
VERSION="${SKIASHARP_PR3779_VERSION:-4.147.0-pr.3779.1}"
VERSION_SUFFIX=""
if [[ "$VERSION" == *-* ]]; then
  VERSION_SUFFIX="${VERSION#*-}"
fi
BUILD_ID="${SKIASHARP_PR3779_BUILD_ID:-}"
FEED_DIR="${SKIASHARP_PR3779_PACKAGES:-$HOME/.skiasharp/hives/pr-${PR_NUMBER}/packages}"
WORK_DIR="${SKIASHARP_PR3779_WORK:-$HOME/.skiasharp/hives/pr-${PR_NUMBER}/source}"
ARTIFACT_DIR="${SKIASHARP_PR3779_ARTIFACTS:-$HOME/.skiasharp/hives/pr-${PR_NUMBER}/artifacts}"
SKIASHARP_REPO="${SKIASHARP_PR3779_REPO:-https://github.com/mono/SkiaSharp.git}"
AZDO_BASE="https://dev.azure.com/xamarin/public/_apis"
PIPELINE_ID="4"

require_tool() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing required tool: $1" >&2
    exit 1
  fi
}

require_tool curl
require_tool dotnet
require_tool git
require_tool jq
require_tool python3
require_tool unzip

mkdir -p "$FEED_DIR" "$ARTIFACT_DIR"
PATCH_MARKER="$FEED_DIR/.skpath-finalizer-patched-$VERSION"

if [[ -f "$FEED_DIR/SkiaSharp.$VERSION.nupkg" && -f "$FEED_DIR/SkiaSharp.NativeAssets.macOS.$VERSION.nupkg" && -f "$PATCH_MARKER" ]]; then
  echo "SkiaSharp PR $PR_NUMBER packages already exist in $FEED_DIR"
  package_version_dir="${VERSION,,}"
  rm -rf "$HOME/.nuget/packages/skiasharp/$package_version_dir"
  rm -rf "$HOME/.nuget/packages/skiasharp.nativeassets.macos/$package_version_dir"
  exit 0
fi

echo "Attempting official SkiaSharp PR artifact download..."
if curl -fsSL https://raw.githubusercontent.com/mono/SkiaSharp/main/scripts/get-skiasharp-pr.sh | bash -s -- "$PR_NUMBER" --force; then
  if [[ -f "$FEED_DIR/SkiaSharp.$VERSION.nupkg" ]]; then
    echo "Official SkiaSharp PR package feed was downloaded. Repacking locally to apply the SKPath finalizer safety patch."
  fi
fi

echo "Building local PR feed from source and native macOS artifact."

if [[ ! -d "$WORK_DIR/.git" ]]; then
  mkdir -p "$(dirname "$WORK_DIR")"
  git clone --depth 1 "$SKIASHARP_REPO" "$WORK_DIR"
fi

git -C "$WORK_DIR" fetch --depth 1 origin "pull/$PR_NUMBER/head:pr-$PR_NUMBER"
git -C "$WORK_DIR" checkout "pr-$PR_NUMBER"

python3 - "$WORK_DIR/binding/SkiaSharp/SKPath.cs" <<'PY'
from pathlib import Path
import sys

path = Path(sys.argv[1])
text = path.read_text()
if "private bool _isDisposing;" not in text:
    text = text.replace(
        "private SKPathBuilder _builder;",
        "private SKPathBuilder _builder;\n\t\tprivate bool _isDisposing;")
    text = text.replace(
        """\t\tpublic override IntPtr Handle {
\t\t\tget {
\t\t\t\tFlushBuilder ();
\t\t\t\treturn base.Handle;
\t\t\t}
\t\t\tprotected set => base.Handle = value;
\t\t}""",
        """\t\tpublic override IntPtr Handle {
\t\t\tget {
\t\t\t\tif (!_isDisposing)
\t\t\t\t\tFlushBuilder ();
\t\t\t\treturn base.Handle;
\t\t\t}
\t\t\tprotected set => base.Handle = value;
\t\t}""")
    text = text.replace(
        """\t\tprotected override void Dispose (bool disposing) =>
\t\t\tbase.Dispose (disposing);""",
        """\t\tprotected override void Dispose (bool disposing)
\t\t{
\t\t\t_isDisposing = true;
\t\t\tbase.Dispose (disposing);
\t\t}""")
    text = text.replace(
        "\t\t\tSkiaApi.sk_path_delete (Handle);",
        "\t\t\tSkiaApi.sk_path_delete (base.Handle);")
    path.write_text(text)
PY

if [[ -z "$BUILD_ID" ]]; then
  echo "Discovering latest Azure Pipelines build for PR #$PR_NUMBER..."
  builds_json="$ARTIFACT_DIR/builds.json"
  curl -fsSL "$AZDO_BASE/build/builds?api-version=7.1&definitions=$PIPELINE_ID&reasonFilter=pullRequest&\$top=200" -o "$builds_json"
  BUILD_ID=$(jq -r --arg branch "refs/pull/$PR_NUMBER/merge" '
    .value
    | map(select(.sourceBranch == $branch))
    | sort_by(.queueTime)
    | reverse
    | .[0].id // empty
  ' "$builds_json")
fi

if [[ -z "$BUILD_ID" ]]; then
  echo "Unable to find an Azure Pipelines build for PR #$PR_NUMBER" >&2
  exit 1
fi

echo "Using Azure Pipelines build $BUILD_ID"
artifacts_json="$ARTIFACT_DIR/artifacts-$BUILD_ID.json"
curl -fsSL "$AZDO_BASE/build/builds/$BUILD_ID/artifacts?api-version=7.1" -o "$artifacts_json"
macos_url=$(jq -r '.value[] | select(.name == "native_macos_macos") | .resource.downloadUrl // empty' "$artifacts_json")

if [[ -z "$macos_url" ]]; then
  echo "Build $BUILD_ID does not contain native_macos_macos artifact" >&2
  exit 1
fi

zip_path="$ARTIFACT_DIR/native_macos_macos-$BUILD_ID.zip"
extract_dir="$ARTIFACT_DIR/native_macos_macos-$BUILD_ID"
if [[ ! -f "$zip_path" ]]; then
  echo "Downloading native_macos_macos artifact..."
  curl -fSL --progress-bar "$macos_url" -o "$zip_path"
fi

rm -rf "$extract_dir"
unzip -q "$zip_path" -d "$extract_dir"
mkdir -p "$WORK_DIR/output/native/osx"
cp "$extract_dir/native_macos_macos/native/osx/libSkiaSharp.dylib" "$WORK_DIR/output/native/osx/libSkiaSharp.dylib"

if ! nm -gU "$WORK_DIR/output/native/osx/libSkiaSharp.dylib" | grep -q "_sk_mesh_make_indexed"; then
  echo "Downloaded libSkiaSharp.dylib does not export sk_mesh_make_indexed" >&2
  exit 1
fi

echo "Building SkiaSharp.DotNet.Interactive helper required by the SkiaSharp pack target..."
dotnet build "$WORK_DIR/source/SkiaSharp.DotNet.Interactive/SkiaSharp.DotNet.Interactive.csproj" \
  -c Release \
  -p:TargetFrameworks=netstandard2.1

echo "Packing SkiaSharp $VERSION..."
dotnet pack "$WORK_DIR/binding/SkiaSharp/SkiaSharp.csproj" \
  -c Release \
  -p:TargetFrameworks=net10.0 \
  -p:BuildingInsideVisualStudio=true \
  -p:VersionSuffix="$VERSION_SUFFIX" \
  -p:ContinuousIntegrationBuild=false \
  -o "$FEED_DIR"

echo "Packing SkiaSharp.NativeAssets.macOS $VERSION..."
dotnet pack "$WORK_DIR/binding/SkiaSharp.NativeAssets.macOS/SkiaSharp.NativeAssets.macOS.csproj" \
  -c Release \
  -p:TargetFrameworks=net10.0 \
  -p:VersionSuffix="$VERSION_SUFFIX" \
  -p:ContinuousIntegrationBuild=false \
  -o "$FEED_DIR"

package_version_dir="${VERSION,,}"
rm -rf "$HOME/.nuget/packages/skiasharp/$package_version_dir"
rm -rf "$HOME/.nuget/packages/skiasharp.nativeassets.macos/$package_version_dir"
touch "$PATCH_MARKER"

echo "SkiaSharp PR $PR_NUMBER local feed is ready: $FEED_DIR"
