#!/usr/bin/env bash
# The samples of ../test.sh, built for real .NET (CoreCLR), run, and checked the
# same way: exit code 0, and the output tests/expected holds.
#
# By default with lib/ compiled into the program (dotnet/Faithful): its Task,
# timers, streams, sockets and Console take the place of the BCL's, so the
# program behaves as in zero mode (Task.Run starts a fiber, a socket lets the
# other fibers run). Not its SHA-2, HMAC, HKDF, ChaCha20-Poly1305 and random
# numbers: those are .NET's, which are the platform's. With --bcl-types those are .NET's own, for a program
# that hands them to other .NET code: the samples then reference
# dotnet/ZeroCompat, lib/ without what .NET has.
#
# Two lists say where real .NET can't give zero mode's answer:
# - HEAP_MEASURED samples print how many bytes the heap grew, which is 0 only
#   where objects go to arenas: only their exit code is checked.
# - dotnet/known-differences.txt (known-differences-bcl.txt with --bcl-types)
#   names checks that fail on .NET for a reason given there
#   ("Sample|check|reason"). A sample with entries passes when exactly those
#   checks fail; an entry whose check passes is an error, so the list can't
#   go stale.
#   "Sample|(sample)|reason" is a sample that doesn't run on .NET at all.
#
# Exit code: the number of samples that didn't do as the lists say.
#
# Usage:  dotnet/test.sh [--bcl-types] [--gc-stress] [Sample...]
#                                        (default: every sample ../test.sh runs)
#   --gc-stress   while each program runs, a thread makes .NET's GC collect
#                 and compact every couple of milliseconds, so objects move
#                 all the time: for finding code in lib/ that assumed they
#                 never do.
# Env:    JOBS (parallel builds, default 6), ZERO_TEST_TIMEOUT (seconds, default 120)
set -uo pipefail
cd "$(dirname "$0")/.."
ROOT=$PWD

# test.sh's SAMPLES list, without running test.sh.
mapfile_samples() {
    awk '/^SMALL_SAMPLES=\(/{s=1;next} s&&/^\)/{s=0} s{print $1}' test.sh > /tmp/.zs.$$
    awk '/^SAMPLES=\(/{s=1;next} s&&/^\)/{s=0} s&&$1!~/"/{print $1}' test.sh
    cat /tmp/.zs.$$; rm -f /tmp/.zs.$$
}
PROJECT=dotnet/Faithful/Faithful.csproj
BIN=dotnet/Faithful/bin
KNOWN=dotnet/known-differences.txt
BCL=
while [ $# -gt 0 ]; do
    case "$1" in
        --bcl-types)
            BCL=1
            PROJECT=dotnet/Sample/Sample.csproj
            BIN=dotnet/Sample/bin
            KNOWN=dotnet/known-differences-bcl.txt ;;
        --gc-stress) export ZERO_GC_STRESS=1 ;;
        *) break ;;
    esac
    shift
done
export PROJECT
if [ $# -gt 0 ]; then SAMPLES=("$@"); else SAMPLES=($(mapfile_samples)); fi
TIMED=" Simd Kernels Fibers Tasks "
HEAP_MEASURED=" Arena Collections TensorTypes "

# The checks a run reported as failed, one per line, sorted ("  failed: x").
# The samples print "  failed: <check>", "  FAIL: <check>" (Core) or "FAIL <check>"
# (Catches); a "FAIL <section> (n checks)" line isn't a check.
failed_checks() { grep -vE '^FAIL .* \([0-9]+ checks\)$' | sed -nE -e 's/^ *failed: (.*)$/\1/p' -e 's/^ *FAIL: (.*)$/\1/p' -e 's/^FAIL (.*)$/\1/p' | sort -u; }
# Compares the failed checks (standard input) with a sample's entries ($1) and
# prints what doesn't agree; nothing if all is as listed. An entry ending in
# "*" stands for every check that starts so (for names with a number in
# them); one starting with "?" may fail or not (it tests what .NET leaves
# open, such as what fresh memory holds).
compare_known() {
    perl -e '
        my @known = split /\n/, shift;
        my (%hit, @extra);
        while (my $failed = <STDIN>) {
            chomp $failed;
            my $found;
            for my $entry (@known) {
                (my $want = $entry) =~ s/^\?//;
                my $match = $want =~ s/\*$// ? index($failed, $want) == 0 : $failed eq $want;
                if ($match) { $hit{$entry} = 1; $found = 1; last; }
            }
            push @extra, $failed unless $found;
        }
        print "fails, not listed: $_\n" for @extra;
        print "listed, passes: $_\n" for grep { !/^\?/ && !$hit{$_} } @known;
    ' "$1"
}
# The checks the list expects to fail for sample $1.
known_checks() { awk -F'|' -v s="$1" '$1 == s && $2 != "(sample)" && $2 != "?(sample)" { print $2 }' "$KNOWN" | sort -u; }
TIMEOUT=${ZERO_TEST_TIMEOUT:-120}
OUT=dotnet/out
mkdir -p "$OUT"

echo "[dotnet] native"
dotnet/native/build.sh
if [ -n "$BCL" ]; then
    echo "[dotnet] ZeroCompat"
    if ! dotnet build -v q -c Release dotnet/ZeroCompat/ZeroCompat.csproj > "$OUT/ZeroCompat.build.txt" 2>&1; then
        grep -E " error " "$OUT/ZeroCompat.build.txt" | sort -u | head -20
        exit 1
    fi
fi

build_one() {
    local name=$1 extra=
    [ "$name" = TensorsAvx ] && extra="-p:SampleFile=Tensors"
    dotnet build -v q -c Release "$PROJECT" -p:Sample="$name" $extra \
        -p:BuildProjectReferences=false > "$OUT/$name.build.txt" 2>&1 && echo ok > "$OUT/$name.built" || rm -f "$OUT/$name.built"
}
export -f build_one; export OUT
for name in "${SAMPLES[@]}"; do
    [ "$name" = Llama ] && scripts/llama-model-cs.sh bin/gen/Stories260K.cs
done
echo "[dotnet] building ${#SAMPLES[@]} samples"
printf '%s\n' "${SAMPLES[@]}" | xargs -P "${JOBS:-6}" -I{} bash -c 'build_one {}'

# judge <sample>: runs it and prints its "ok"/"FAIL"/"BUILD" lines. Returns 0
# if it passed, 1 if checks failed or its output differs, 2 if it didn't
# build, 3 if it stopped (an exception nothing caught, a crash, the time limit).
judge() {
    local name=$1 input status=0 out expected known verdict
    if [ ! -f "$OUT/$name.built" ]; then
        echo "BUILD $name: $(grep -cE ' error ' "$OUT/$name.build.txt" | tr -d ' ') errors"
        grep -E " error " "$OUT/$name.build.txt" | sed -E 's#.*/([A-Za-z0-9]+\.cs)\(([0-9]+),[0-9]+\).*error (CS[0-9]+): (.*) \[.*#       \1:\2 \3 \4#' | sort -u | head -4
        return 2
    fi
    input=tests/input/$name.txt; [ -f "$input" ] || input=/dev/null
    out=$(perl -e 'alarm shift; exec @ARGV' "$TIMEOUT" dotnet "$BIN/$name/Release/net11.0/$name.dll" < "$input" 2>&1) || status=$?
    echo "$out" > "$OUT/$name.out.txt"
    expected=tests/expected/$name.txt
    known=$(known_checks "$name")
    if [ "$status" = 142 ]; then echo "FAIL $name: timed out"; return 3
    elif [ -n "$known" ]; then
        # Exactly the listed checks must fail (and nothing may crash: a
        # sample's exit code is its number of failed checks).
        verdict=$(echo "$out" | failed_checks | compare_known "$known")
        if [ -z "$verdict" ] && [ "$status" -lt 100 ]; then
            echo "ok   $name ($(echo "$known" | grep -vc '^?') known differences)"; return 0
        fi
        echo "FAIL $name: exit code $status; failed checks aren't the ones in $KNOWN"
        echo "$verdict" | head -8 | sed 's/^/       /'
        echo "$out" | tail -2 | sed 's/^/       /'
        [ "$status" -ge 100 ] && return 3
        return 1
    elif [ "$status" != 0 ]; then
        echo "FAIL $name: exit code $status"
        { echo "$out" | grep -E '^ *(failed:|FAIL )' | head -5; echo "$out" | tail -3; } | sed 's/^/       /'
        [ "$status" -ge 100 ] && return 3
        return 1
    elif [[ "$TIMED$HEAP_MEASURED" == *" $name "* ]]; then echo "ok   $name (exit code only)"; return 0
    elif [ "$out" != "$(cat "$expected" 2>/dev/null)" ]; then
        echo "FAIL $name: output differs from $expected"
        diff <(echo "$out") "$expected" | head -8 | sed 's/^/       /'
        return 1
    fi
    echo "ok   $name"
}

# A sample the list names as a whole ("Sample|(sample)|reason") doesn't run on
# .NET at all, for the reason given: it not building or failing is expected,
# and its passing is the error. "?(sample)" is one that doesn't build or
# stops on some systems only: where it runs to its end, its checks are
# judged like any other sample's.
pass=0; fail=0; nobuild=0; known_whole=0
for name in "${SAMPLES[@]}"; do
    whole=$(awk -F'|' -v s="$name" '$1 == s && ($2 == "(sample)" || $2 == "?(sample)") { print $2 "|" $3; exit }' "$KNOWN")
    report=$(judge "$name"); result=$?
    if [ -z "$whole" ]; then
        echo "$report"
        case $result in 0) pass=$((pass + 1)) ;; 1) fail=$((fail + 1)) ;; *) nobuild=$((nobuild + 1)) ;; esac
    elif [ "${whole%%|*}" = "(sample)" ]; then
        if [ $result = 0 ]; then
            echo "FAIL $name: it passes, and $KNOWN says it doesn't run on .NET (${whole#*|})"
            fail=$((fail + 1))
        else
            echo "known $name: ${whole#*|}"
            known_whole=$((known_whole + 1))
        fi
    elif [ $result -ge 2 ]; then
        echo "known $name: ${whole#*|}"
        known_whole=$((known_whole + 1))
    else
        echo "$report"
        case $result in 0) pass=$((pass + 1)) ;; *) fail=$((fail + 1)) ;; esac
    fi
done
echo
echo "pass $pass, fail $fail, don't build $nobuild, known not to run $known_whole (of ${#SAMPLES[@]})"
# The exit code: how many samples didn't do as expected.
unexpected=$((fail + nobuild))
exit $((unexpected > 250 ? 250 : unexpected))
