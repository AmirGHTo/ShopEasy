#!/usr/bin/env bash
# Point every service image at one published build.
#   k8s/set-image-tag.sh sha-abc1234
# The tag is the short commit hash shown on the CI/CD run ("Publish image" jobs) and in the
# package list at ghcr.io/<owner>/shopeasy-<service>. `latest` is refused on purpose.
set -euo pipefail

tag="${1:-}"
if ! [[ "$tag" =~ ^sha-[0-9a-f]{7,40}$ ]]; then
  echo "usage: $0 sha-<commit hash, 7-40 hex characters>   (not 'latest', not a branch name)" >&2
  exit 2
fi

file="$(dirname "$0")/kustomization.yaml"
sed -i -E "s/^([[:space:]]*newTag:[[:space:]]*).*/\1${tag}/" "$file"
echo "Every image in $file now uses ${tag}:"
grep -nE 'newName|newTag' "$file"
