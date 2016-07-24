#!/bin/bash
set -eu

BUILD="Release"

UNTIL_CLEAN_EXIT=0

ASF_ARGS=()
MONO_ARGS=("--llvm" "--server" "-O=all")

PRINT_USAGE() {
	echo "Usage: $0 [--until-clean-exit] [--cryptkey=] [--path=] [--server] [debug/release]"
	exit 1
}

for ARG in "$@"; do
	case "$ARG" in
		release|Release) BUILD="Release" ;;
		debug|Debug) BUILD="Debug" ;;
		--cryptkey=*) ASF_ARGS+=("--cryptkey=$(echo "$ARG" | cut -d '=' -f 2-)") ;;
		--path=*) ASF_ARGS+=("--path=$(echo "$ARG" | cut -d '=' -f 2-)") ;;
		--server) ASF_ARGS+=("--server") ;;
		--until-clean-exit) UNTIL_CLEAN_EXIT=1 ;;
		*) PRINT_USAGE
	esac
done

if [[ "$BUILD" = "Debug" ]]; then
	MONO_ARGS+=("--debug")
fi

cd "$(dirname "$(readlink -f "$0")")"

BINARY="ArchiSteamFarm/bin/$BUILD/ArchiSteamFarm.exe"

if [[ ! -f "$BINARY" ]]; then
	echo "ERROR: $BINARY could not be found!"
	exit 1
fi

if [[ "$UNTIL_CLEAN_EXIT" -eq 0 ]]; then
	mono "${MONO_ARGS[@]}" "$BINARY" "${ASF_ARGS[@]}"
	exit $?
fi

while [[ -f "$BINARY" ]]; do
	if mono "${MONO_ARGS[@]}" "$BINARY" "${ASF_ARGS[@]}"; then
		break
	fi
	sleep 1
done
