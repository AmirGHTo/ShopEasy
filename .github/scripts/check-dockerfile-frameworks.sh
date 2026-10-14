#!/usr/bin/env bash
# Every Dockerfile's .NET base image (sdk / aspnet) must be the version the projects target.
#
# Why this exists: a Docker image BUILD succeeds with a newer SDK and any runtime image, so
# the CI docker-build job stays green — but a .NET 10 runtime cannot run a net8.0 application,
# and the container fails the moment it starts. A routine dependency update (Dependabot bumping
# the image tag) therefore produces a green pull request that breaks every deployment.
# Run from the repository root.
set -euo pipefail

tfms=$(grep -rhoE '<TargetFramework>net[0-9]+\.[0-9]+' src --include='*.csproj' \
        | sed 's/<TargetFramework>net//' | sort -u)

if [ -z "$tfms" ]; then
  echo "::error::No <TargetFramework> found under src/"
  exit 1
fi
if [ "$(printf '%s\n' "$tfms" | wc -l)" -ne 1 ]; then
  echo "::error::The projects target more than one framework version ($(echo $tfms)); this check expects one."
  exit 1
fi

expected="$tfms"
status=0
checked=0

while IFS= read -r hit; do
  file=${hit%%:*}
  rest=${hit#*:}
  line=${rest%%:*}
  tag=$(printf '%s' "${rest#*:}" | sed -E 's#.*dotnet/(sdk|aspnet|runtime):([^ ]+).*#\2#')
  checked=$((checked + 1))
  case "$tag" in
    "$expected"|"$expected".*|"$expected"-*) ;;                       # 8.0, 8.0.x, 8.0-bookworm-slim
    *)
      echo "::error file=$file,line=$line::Base image tag '$tag' does not match the projects' target framework net$expected"
      status=1
      ;;
  esac
done < <(grep -rnE '^FROM mcr\.microsoft\.com/dotnet/(sdk|aspnet|runtime):' src --include=Dockerfile)

if [ "$checked" -eq 0 ]; then
  echo "::error::No .NET base images found in any Dockerfile under src/ — is the check looking in the right place?"
  exit 1
fi

if [ "$status" -eq 0 ]; then
  echo "OK: $checked .NET base image lines, all net$expected"
fi
exit "$status"
