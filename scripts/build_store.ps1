dotnet build -f net10.0-ios -c Release \
  -p:RuntimeIdentifier=ios-arm64 \
  -p:ArchiveOnBuild=true \
  -p:CodesignKey="iPhone Distribution: Tim Edalatpour (QNQ323Z7FW)" \
  -p:CodesignProvision="BenPlannerAppStore"