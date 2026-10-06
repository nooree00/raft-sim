# P9-08: the host, built and run in the SDK image CI already pins (phase 9 decision 9: no second
# image is pinned or pulled). SDK_IMAGE is ci/image.digest, passed by scripts/compose-run.sh.
ARG SDK_IMAGE
FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Raft.Core src/Raft.Core
COPY src/Raft.Kv src/Raft.Kv
COPY src/Raft.Host src/Raft.Host
RUN dotnet publish src/Raft.Host -c Release -o /app -p:RestoreLockedMode=true

FROM ${SDK_IMAGE}
COPY --from=build /app /app
ENTRYPOINT ["dotnet", "/app/Raft.Host.dll"]
