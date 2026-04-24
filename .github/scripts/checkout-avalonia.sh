#!/usr/bin/env bash
set -euo pipefail

: "${AVALONIA_REPOSITORY:=https://github.com/wieslawsoltes/Avalonia.git}"
: "${AVALONIA_REF:=1060839683d2f8bb1b752d5bc021a53f90a0129d}"
: "${AvaloniaSourceRoot:?AvaloniaSourceRoot must point to the checkout destination}"

if [ -d "$AvaloniaSourceRoot/.git" ]; then
  git -C "$AvaloniaSourceRoot" fetch --filter=blob:none origin "$AVALONIA_REF" || true
else
  rm -rf "$AvaloniaSourceRoot"
  git clone --filter=blob:none "$AVALONIA_REPOSITORY" "$AvaloniaSourceRoot"
fi

if ! git -C "$AvaloniaSourceRoot" checkout --detach "$AVALONIA_REF"; then
  git -C "$AvaloniaSourceRoot" fetch --filter=blob:none origin "$AVALONIA_REF"
  git -C "$AvaloniaSourceRoot" checkout --detach FETCH_HEAD
fi

git -C "$AvaloniaSourceRoot" submodule update --init --recursive --depth 1

echo "AvaloniaSourceRoot=$AvaloniaSourceRoot" >> "$GITHUB_ENV"
echo "Avalonia source: $(git -C "$AvaloniaSourceRoot" rev-parse HEAD)"
