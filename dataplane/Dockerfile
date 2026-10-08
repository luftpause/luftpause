# syntax=docker/dockerfile:1.7
FROM rust:1-bookworm AS build
RUN apt-get update \
 && apt-get install -y --no-install-recommends protobuf-compiler \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY proto ./proto
COPY dataplane ./dataplane
WORKDIR /src/dataplane
RUN --mount=type=cache,target=/usr/local/cargo/registry \
    --mount=type=cache,target=/src/dataplane/target \
    cargo build --release --locked \
 && mkdir -p /out \
 && cp target/release/dataplane target/release/fake-engine target/release/fake-mcp /out/

FROM debian:bookworm-slim
COPY --from=build /out/ /usr/local/bin/
