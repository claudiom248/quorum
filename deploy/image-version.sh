#!/bin/sh
# Prints the version of this checkout as SemVer, from git describe: the Docker build context has no git history,
# so the image gets its version from here (see compose.sh).
#
#   v0.6.0 exactly         ->  0.6.0
#   3 commits after v0.6.0 ->  0.6.1-alpha.0.3+<sha>   (what MinVer would compute)
#   uncommitted changes    ->  the same with ".dirty" appended to the build metadata
#   no tag at all          ->  0.0.0-local+<sha>
#
# An argument stands in for the output of git describe (used by the tests).

if [ "$#" -gt 0 ]; then
  describe="$1"
else
  describe=$(git describe --tags --long --match 'v[0-9]*' --always --dirty 2>/dev/null)
fi

dirty=0
case "$describe" in
  *-dirty)
    dirty=1
    describe=${describe%-dirty}
    ;;
esac

fields=$(printf '%s' "$describe" | sed -n 's/^v\{0,1\}\([0-9][0-9]*\)\.\([0-9][0-9]*\)\.\([0-9][0-9]*\)-\([0-9][0-9]*\)-g\([0-9a-f][0-9a-f]*\)$/\1 \2 \3 \4 \5/p')

if [ -n "$fields" ]; then
  # shellcheck disable=SC2086
  set -- $fields
  major=$1 minor=$2 patch=$3 height=$4 sha=$5
  if [ "$height" -eq 0 ] && [ "$dirty" -eq 0 ]; then
    printf '%s.%s.%s\n' "$major" "$minor" "$patch"
    exit 0
  fi
  version="$major.$minor.$((patch + 1))-alpha.0.$height+$sha"
else
  version="0.0.0-local"
  if [ -n "$describe" ]; then
    version="$version+$describe"
  fi
fi

if [ "$dirty" -eq 1 ]; then
  case "$version" in
    *+*) version="$version.dirty" ;;
    *) version="$version+dirty" ;;
  esac
fi

printf '%s\n' "$version"
