#!/bin/bash

# The start script of grafana/otel-lgtm:0.8.1, with one change: the image exports GF_AUTH_ANONYMOUS_ENABLED=true and
# GF_AUTH_ANONYMOUS_ORG_ROLE=Admin here, which gives every visitor write access and makes the Grafana password pointless
# (spec 016 D-6). Those two lines are removed; anonymous access stays off (Grafana's default). When the image tag in
# docker-compose.yml changes, compare this file with /otel-lgtm/run-grafana.sh of the new image.

source ./logging.sh

export GF_PATHS_HOME=/data/grafana
export GF_PATHS_DATA=/data/grafana/data
export GF_PATHS_PLUGINS=/data/grafana/plugins


cd ./grafana
run_with_logging "Grafana ${GRAFANA_VERSION}" "${ENABLE_LOGS_GRAFANA:-false}" ./bin/grafana server
