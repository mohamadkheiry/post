#!/bin/sh
set -eu

test -n "${MINIO_ROOT_USER:-}"
test -n "${MINIO_ROOT_PASSWORD:-}"
mc alias set post-minio http://minio:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null
mc mb --ignore-existing post-minio/post-images >/dev/null
mc anonymous set none post-minio/post-images >/dev/null
mc version enable post-minio/post-images >/dev/null
mc encrypt set sse-s3 post-minio/post-images >/dev/null
encryption_info="$(mc encrypt info post-minio/post-images)"
case "${encryption_info}" in
  *sse-s3*|*SSE-S3*) ;;
  *)
    echo "MinIO bucket default SSE-S3 encryption was not enabled." >&2
    exit 1
    ;;
esac
