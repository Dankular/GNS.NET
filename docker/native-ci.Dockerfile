FROM mcr.microsoft.com/dotnet/sdk:9.0-noble AS native

RUN apt-get update \
    && apt-get install -y --no-install-recommends cmake ninja-build git libssl-dev protobuf-compiler pkg-config \
    && rm -rf /var/lib/apt/lists/*

ARG GNS_REF=master
WORKDIR /src
RUN git clone --depth 1 --branch ${GNS_REF} https://github.com/ValveSoftware/GameNetworkingSockets.git native/GameNetworkingSockets \
    && cmake -S native/GameNetworkingSockets -B native/build -G Ninja -DCMAKE_BUILD_TYPE=Release -DGNS_BUILD_TESTS=OFF -DGNS_BUILD_EXAMPLES=OFF \
    && cmake --build native/build --parallel \
    && mkdir -p /opt/gns/lib \
    && find native/build -type f \( -name 'libGameNetworkingSockets.so*' -o -name 'libsteamnetworkingsockets.so*' \) -exec cp -v {} /opt/gns/lib/ \;

FROM native AS verify
WORKDIR /workspace
COPY . .
ENV LD_LIBRARY_PATH=/opt/gns/lib
RUN dotnet restore GnsNet.sln
RUN dotnet build GnsNet.sln -c Release --no-restore
RUN dotnet test GnsNet.sln -c Release --no-build --verbosity normal
ENTRYPOINT ["dotnet", "run", "--project", "benchmarks/GnsNet.Benchmarks", "-c", "Release", "--no-build", "--", "--scenario", "transport", "--native-path", "/opt/gns/lib/libGameNetworkingSockets.so", "--clients", "2", "--iterations", "1000", "--payload-bytes", "64"]
