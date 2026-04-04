#!/usr/bin/env bash

set -eu

samples_link="project-demo/Assets/_Samples"
samples_target="../../package/Samples~"

if [ -e "$samples_link" ] || [ -L "$samples_link" ]; then
  rm -rf "$samples_link"
fi

ln -s "$samples_target" "$samples_link"
echo "Linked $samples_link -> $samples_target"