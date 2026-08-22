#!/usr/bin/env bash
set -euo pipefail

topics=(
  parcel.events.received
  parcel.events.received.dlq
  image.metadata.received
  image.metadata.received.dlq
  image.metadata.pending.dlq
  parcel.lifecycle.reading-recorded
  parcel.lifecycle.bag-stored
  parcel.lifecycle.dispatch-stored
  config.published
  edge.operational-event-recorded
  edge.disabled
  opresult.stored
  edge.integration-error.v1
)

existing="$(rpk topic list --brokers redpanda:9092 --format json)"
for topic in "${topics[@]}"; do
  if ! grep -Fq "\"${topic}\"" <<<"${existing}"; then
    rpk topic create "${topic}" --brokers redpanda:9092 --partitions 3 --replicas 1 >/dev/null
  fi
done

rpk topic list --brokers redpanda:9092 >/dev/null
