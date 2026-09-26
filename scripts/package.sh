#!/bin/sh
# Builds the zip that Chrome and Brave load, from what is committed.
#
# git archive is used because it writes zip entry names with forward slashes,
# which the zip format requires. PowerShell's Compress-Archive writes
# backslashes instead: Windows Explorer tolerates that, but Brave's own
# unzipper, used when a zip is dropped onto brave://extensions, does not, and
# fails with "Could not load icon 'icons/icon16.png'". Every zip from v3.12.2
# to v3.14.0 had that defect.
set -e
cd "$(dirname "$0")/.."
version=$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' manifest.json)
out="prompt-navigator-$version.zip"
git archive --format=zip -o "$out" HEAD \
  manifest.json claude-prompt-navigator.user.js chatgpt-usage.user.js \
  icons README.md LICENSE
echo "$out"
