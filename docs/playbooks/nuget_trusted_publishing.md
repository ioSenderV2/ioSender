# Publish a .NET library to NuGet via Trusted Publishing

**For:** turning a .NET library into a NuGet package published to nuget.org from GitHub Actions
with **Trusted Publishing (OIDC)** — no stored API key (nuget.org now discourages API keys).
Used to publish `WpfUiTestServer` (a legacy net462 WPF class library).

## Steps

1. **Make the project packable (SDK-style).** A legacy (`.csproj` with
   `<Import ...Microsoft.CSharp.targets>`) project can't `dotnet pack` cleanly — convert to
   SDK-style. For a **net462 WPF lib with no XAML** (like WpfUiTestServer) there's no `<UseWPF>`;
   reference the framework assemblies directly and keep the hand-written AssemblyInfo:
   ```xml
   <Project Sdk="Microsoft.NET.Sdk">
     <PropertyGroup>
       <TargetFramework>net462</TargetFramework>
       <GenerateAssemblyInfo>false</GenerateAssemblyInfo>   <!-- keep existing AssemblyInfo.cs -->
       <PackageId>…</PackageId>
       <Version>1.0.0</Version>
       <Authors>stevenrwood</Authors>
       <Description>…</Description>
       <PackageLicenseExpression>BSD-3-Clause</PackageLicenseExpression>
       <PackageReadmeFile>README.md</PackageReadmeFile>
       <RepositoryUrl>https://github.com/stevenrwood/<name>.git</RepositoryUrl>
       <IncludeSymbols>true</IncludeSymbols>
       <SymbolPackageFormat>snupkg</SymbolPackageFormat>
     </PropertyGroup>
     <ItemGroup> <!-- WPF/UIAutomation from the 4.6.2 targeting pack -->
       <Reference Include="WindowsBase" /> <Reference Include="PresentationCore" />
       <Reference Include="PresentationFramework" /> <Reference Include="System.Xaml" />
       <Reference Include="UIAutomationProvider" /> <Reference Include="UIAutomationTypes" />
     </ItemGroup>
     <ItemGroup><None Include="README.md" Pack="true" PackagePath="\" /></ItemGroup>
   </Project>
   ```

2. **Verify the pack locally** (dotnet 10 is installed):
   ```sh
   cd /c/github/<name> && dotnet pack -c Release
   unzip -l bin/Release/<name>.<ver>.nupkg | grep -E "lib/|\.md"   # expect lib/net462/*.dll + README.md
   ```

3. **Add the CI workflow** `.github/workflows/publish.yml` — pack on push, publish on `v*` tag via
   OIDC. The publish job needs `permissions: id-token: write`, `NuGet/login@v1` (with
   `user: <nuget.org username>`), then push using the temp key:
   ```yaml
   - uses: NuGet/login@v1
     id: login
     with: { user: stevenrwood }
   - name: Push to NuGet
     shell: pwsh
     run: |
       $pkg = (Get-ChildItem artifacts/*.nupkg | Where-Object Extension -eq '.nupkg' | Select-Object -First 1).FullName
       dotnet nuget push $pkg -k ${{ steps.login.outputs.NUGET_API_KEY }} -s https://api.nuget.org/v3/index.json --skip-duplicate
   ```
   > **GOTCHA:** PowerShell does **not** expand `artifacts/*.nupkg` for `dotnet nuget push`
   > (`error: File does not exist`). Resolve the path with `Get-ChildItem` first, as above.

4. **Register the Trusted Publishing policy** on nuget.org (one-time, browser):
   nuget.org → Account → **Trusted Publishing** → Add → Repository owner `stevenrwood`,
   Repository `<name>`, Workflow `publish.yml`. A policy can be created **before** the package
   exists; the first push claims the ID. (Verify the account email too.)

5. **Publish** — tag and push:
   ```sh
   git tag v1.0.0 && git push origin v1.0.0
   ```
   Watch it: `gh run watch <id> --repo stevenrwood/<name> --exit-status`. Both jobs green = pushed.

6. **Wait for indexing** (~5–15 min) before depending on it — new packages go through nuget.org
   validation. Check: `curl -s https://api.nuget.org/v3-flatcontainer/<name-lowercase>/index.json`
   (404/BlobNotFound until indexed).

7. **Repoint consumers** once it's live: replace the `<ProjectReference>` with
   `<PackageReference Include="<name>" Version="1.0.0" />`, drop the in-repo project from the
   `.sln`, and **build-verify** ([headless_build.md](headless_build.md)). Never repoint before the
   package restores, or the build breaks.

## Notes
- Trusted Publishing replaces API keys for GitHub Actions; keys still work for local
  `dotnet nuget push` but are discouraged.
- Re-publishing the same version is a no-op with `--skip-duplicate`; bump `<Version>` + tag `vX.Y.Z`.
