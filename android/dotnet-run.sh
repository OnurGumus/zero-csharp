#!/bin/sh
# dotnet run's launcher for an SDK project's Android program (ZeroTarget
# android-arm64, Sdk.targets), which has already copied it to the device:
# runs <dir>/<name> there through adb shell, with this terminal's input,
# output and exit code. adb shell joins its arguments into one command line
# for the device's shell, so each argument is quoted for it: '...', with '
# as '\''. (As android/run.sh does for ./run.sh --android.)
#
# Usage: dotnet-run.sh <adb> <dir on the device> <name> [args...]
adb=$1
dir=$2
name=$3
shift 3
quote() { printf "'%s'" "$(printf '%s' "$1" | sed "s/'/'\\\\''/g")"; }
command="cd $(quote "$dir") && chmod 755 ./$(quote "$name") && ./$(quote "$name")"
for argument in "$@"; do
    command="$command $(quote "$argument")"
done
exec "$adb" shell -T "$command"
