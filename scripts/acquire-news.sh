#!/usr/bin/env bash
# Downloads the news history for the universe. Deterministic and resumable.
#
# Why this script exists: the news step of acquire-history.sh (branch
# phase-3-historical-ml-expert) passes --limit 50 and never follows
# next_page_token, so it captured one page per symbol -- between one and eight
# days of headlines. The agents read news text in replay, and after ADR-013 that
# text is the only remaining alpha channel, so a single page is not enough.
#
# This path calls the Alpaca REST API with curl rather than the CLI, because the
# CLI binary is git-ignored and absent from a fresh clone. ADR-001 scopes offline
# acquisition to the non-trading path; no application code calls this.
#
# Output: data/raw/news/<SYM>.json, one JSON page object per API page, appended.
# That is the same concatenated-page format the bars files use and that
# RawJsonPages reads with JsonReaderOptions.AllowMultipleValues.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RAW="$ROOT/data/raw"
NEWS_API="https://data.alpaca.markets/v1beta1/news"

UNIVERSE="${UNIVERSE:-SPY QQQ IWM AAPL MSFT NVDA AMZN META GOOGL TSLA AMD MU INTC}"
START="${START:-2026-02-01}"
END="${END:-2026-08-28}"   # last completed session; never today, so reruns match
PAGE="${PAGE:-50}"         # the news endpoint caps a page at 50
PAGE_CAP="${PAGE_CAP:-400}"

if [ -z "${ALPACA_API_KEY:-}" ]; then
  set -a; . "$ROOT/.env"; set +a
fi
: "${ALPACA_API_KEY:?ALPACA_API_KEY is not set}"
: "${ALPACA_SECRET_KEY:?ALPACA_SECRET_KEY is not set}"

mkdir -p "$RAW/news"

fetch_news() {
  local sym="$1"
  local out="$RAW/news/$sym.json"
  local marker="$RAW/news/$sym.window"
  local want="$START..$END"
  local token="" page=0 resp="" count=0

  # Resumable, but never stale: the sidecar records the window the file covers,
  # so widening START or END refetches instead of trusting a narrower file.
  if [ -f "$marker" ] && [ "$(cat "$marker")" = "$want" ] && [ -s "$out" ]; then
    echo "    $sym: cached ($want)"
    return 0
  fi

  : > "$out.tmp"
  while :; do
    resp=$(curl -sS -G "$NEWS_API" \
      --retry 5 --retry-delay 2 --retry-all-errors --fail-with-body \
      --data-urlencode "symbols=$sym" \
      --data-urlencode "start=${START}T00:00:00Z" \
      --data-urlencode "end=${END}T23:59:59Z" \
      --data-urlencode "limit=$PAGE" \
      --data-urlencode "sort=asc" \
      ${token:+--data-urlencode "page_token=$token"} \
      -H "APCA-API-KEY-ID: $ALPACA_API_KEY" \
      -H "APCA-API-SECRET-KEY: $ALPACA_SECRET_KEY") || {
        echo "    !! $sym failed on page $page" >&2; rm -f "$out.tmp"; return 1; }

    printf '%s\n' "$resp" >> "$out.tmp"
    # `|| true` on both: pipefail turns a grep that matches nothing into a
    # failed assignment, and the final page legitimately has no id and
    # answers "next_page_token":null rather than an empty string.
    count=$((count + $(printf '%s' "$resp" | grep -o '"id":' | wc -l || true)))
    token=$(printf '%s' "$resp" | grep -o '"next_page_token":"[^"]*"' | sed 's/.*:"//;s/"$//' | head -1 || true)
    page=$((page+1))
    [ -z "$token" ] && break
    [ "$page" -ge "$PAGE_CAP" ] && { echo "    !! page cap for $sym" >&2; break; }
  done

  mv "$out.tmp" "$out"
  printf '%s' "$want" > "$marker"
  echo "    $sym: $page page(s), $count items, $(wc -c < "$out") bytes"
}

echo "News $START -> $END"
for SYM in $UNIVERSE; do
  fetch_news "$SYM"
done
echo "-> $RAW/news"
