#!/bin/sh
# docker compose with the version of this checkout stamped on the image (the build context has no git history).
# Run it from the repository root, with the arguments you would give to docker compose:
#
#   sh deploy/compose.sh --profile bundled-db up --build -d
here=$(cd "$(dirname "$0")" && pwd)
QUORUM_VERSION=$(sh "$here/image-version.sh")
export QUORUM_VERSION
exec docker compose "$@"
