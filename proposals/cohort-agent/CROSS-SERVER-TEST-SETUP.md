# Two-server (cross-server) cohort test environment

How to stand up a **second** docker SQL Server and a genuinely cross-server cohort, used to verify
that `ExportCohortAsScript` emits **best-effort `query.sql`** when a cohort's sets span servers and
no QueryCache is configured (RDMP can't `UNION`/`INTERSECT`/`EXCEPT` across servers, so the whole
query can't be generated — each set is emitted individually instead).

This is a **local dev fixture**, not an automated test (CI has no second server). The committed
coverage is the in-memory `TestCohortScriptRoundTrip.Export_WhenFullSqlCannotBeGenerated_…` test;
this doc reproduces the real two-server proof.

## Prerequisites
- The primary server `rdmp-mssql` (port **1433**) from `mac-test-env/docker-compose.yml`, with the
  platform DBs installed (`TEST_Catalogue` / `TEST_DataExport`) and the CLI's `Databases.yaml`
  pointing at it (`Server=localhost,1433;...`). See `.cohort-test/Databases.yaml`.
- A built CLI. Either the in-tree dev build or the v9.2.3 worktree; set `DLL` below to its `rdmp.dll`.

```bash
PW='YourStrong!Passw0rd'
DLL=/path/to/Tools/rdmp/bin/Release/net10.0/rdmp.dll   # <-- set to your build
run() { dotnet "$DLL" cmd "$@" >/dev/null 2>&1; }
maxid() { docker exec rdmp-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$PW" -C -h -1 -W \
  -Q "SET NOCOUNT ON; SELECT ISNULL(MAX(ID),0) FROM TEST_Catalogue.dbo.$1" 2>/dev/null | tr -d '[:space:]'; }
```

## 1. Start the second server (port 1434, its own volume)

```bash
docker run -d --name rdmp-mssql2 --platform linux/amd64 \
  -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD="$PW" -e MSSQL_PID=Developer \
  -p 1434:1433 mcr.microsoft.com/mssql/server:2022-latest

# wait until healthy
until docker exec rdmp-mssql2 /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$PW" -C -Q "SELECT 1" >/dev/null 2>&1; do sleep 5; done
echo "server2 up"
```

## 2. Seed data on both servers

```bash
# server 2 (1434): TEST_Server2.dbo.S2_Patients
docker exec -i rdmp-mssql2 /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$PW" -C -Q "
IF DB_ID('TEST_Server2') IS NULL CREATE DATABASE TEST_Server2;"
docker exec -i rdmp-mssql2 /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$PW" -C -d TEST_Server2 -Q "
IF OBJECT_ID('dbo.S2_Patients') IS NOT NULL DROP TABLE dbo.S2_Patients;
CREATE TABLE dbo.S2_Patients (chi varchar(10));
INSERT INTO dbo.S2_Patients VALUES ('P01'),('P02'),('P03');"

# server 1 (1433): two tables in the existing TEST_ScratchArea
docker exec -i rdmp-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$PW" -C -d TEST_ScratchArea -Q "
IF OBJECT_ID('dbo.XS_S1a') IS NOT NULL DROP TABLE dbo.XS_S1a;
IF OBJECT_ID('dbo.XS_S1b') IS NOT NULL DROP TABLE dbo.XS_S1b;
CREATE TABLE dbo.XS_S1a (chi varchar(10)); INSERT INTO dbo.XS_S1a VALUES ('P01'),('P02'),('P05');
CREATE TABLE dbo.XS_S1b (chi varchar(10)); INSERT INTO dbo.XS_S1b VALUES ('P02'),('P07');"
```

## 3. Build the catalogues as RDMP metadata (no data connection needed)

```bash
mkcata() {  # name  "[db]..[tbl]"  serverPort  dbName
  local cata="$1" fq="$2" port="$3" db="$4"
  run NewObject TableInfo "$fq";            local tid; tid=$(maxid TableInfo)
  run Set "TableInfo:$tid" Server "localhost,$port"
  run Set "TableInfo:$tid" Database "$db"          # <-- CRITICAL, see gotcha below
  run NewObject ColumnInfo "${fq}.[chi]" "varchar(10)" "TableInfo:$tid"; local colid; colid=$(maxid ColumnInfo)
  run NewObject Catalogue "$cata";          local cid; cid=$(maxid Catalogue)
  run NewObject CatalogueItem "Catalogue:$cid" "chi"; local ciid; ciid=$(maxid CatalogueItem)
  run NewObject ExtractionInformation "CatalogueItem:$ciid" "ColumnInfo:$colid" "chi"; local eiid; eiid=$(maxid ExtractionInformation)
  run Set "ExtractionInformation:$eiid" IsExtractionIdentifier true
  echo "Catalogue '$cata'  cid=$cid  TableInfo=$tid  server=localhost,$port  db=$db"
}

mkcata XS_S1a "[TEST_ScratchArea]..[XS_S1a]"   1433 TEST_ScratchArea
mkcata XS_S1b "[TEST_ScratchArea]..[XS_S1b]"   1433 TEST_ScratchArea
mkcata XS_S2  "[TEST_Server2]..[S2_Patients]"  1434 TEST_Server2
```

> **GOTCHA — `NewObject TableInfo` leaves `Database` NULL.** The fully-qualified name (`[db]..[tbl]`)
> is stored in `Name`, but the `Database` column stays NULL, and SQL generation needs it — without it
> the per-set query fails with *"DataAccessPoint '…' does not have a Database specified on it"*. Always
> `Set TableInfo:<id> Database "<db>"` (and `Server`). Differing `Server` values are what make the
> cohort cross-server in the first place.

## 4. Build the two cohorts (via our own BuildCohortFromScript)

`xs-single.yaml` (both sets on server 1 → generates a real combined query):
```yaml
Commands:
  - CreateNewCohortIdentificationConfiguration "XS_SingleServer" => $c1
  - SetContainerOperation CohortAggregateContainer:$c1 UNION
  - AddCatalogueToCohortIdentificationSetContainer CohortAggregateContainer:$c1 Catalogue:"XS_S1a" => $a1
  - AddCatalogueToCohortIdentificationSetContainer CohortAggregateContainer:$c1 Catalogue:"XS_S1b" => $a2
```
`xs-cross.yaml` (one set per server → triggers the cross-server fallback):
```yaml
Commands:
  - CreateNewCohortIdentificationConfiguration "XS_CrossServer" => $c1
  - SetContainerOperation CohortAggregateContainer:$c1 UNION
  - AddCatalogueToCohortIdentificationSetContainer CohortAggregateContainer:$c1 Catalogue:"XS_S1a" => $a1
  - AddCatalogueToCohortIdentificationSetContainer CohortAggregateContainer:$c1 Catalogue:"XS_S2"  => $a2
```
```bash
dotnet "$DLL" cmd BuildCohortFromScript xs-single.yaml "XS_SingleServer"
dotnet "$DLL" cmd BuildCohortFromScript xs-cross.yaml  "XS_CrossServer"
dotnet "$DLL" cmd list CohortIdentificationConfiguration | grep XS_   # note the two CIC ids
```

## 5. Verify

```bash
# CROSS-SERVER -> query.sql is the best-effort document
dotnet "$DLL" cmd ExportCohortAsScript CohortIdentificationConfiguration:<crossId> ./out --skip-patching
cat ./out/XS_CrossServer/query.sql        # expect BEST-EFFORT header, both sets' SQL, UNION comment, notes footer

# each per-set SQL is valid against ITS OWN server:
docker exec -i rdmp-mssql  /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$PW" -C -d TEST_ScratchArea -h -1 -W \
  -Q "SELECT distinct chi FROM [TEST_ScratchArea]..[XS_S1a]"     # -> P01 P02 P05
docker exec -i rdmp-mssql2 /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$PW" -C -d TEST_Server2 -h -1 -W \
  -Q "SELECT distinct chi FROM [TEST_Server2]..[S2_Patients]"    # -> P01 P02 P03

# SINGLE-SERVER -> real combined query.sql (fallback does NOT fire)
dotnet "$DLL" cmd ExportCohortAsScript CohortIdentificationConfiguration:<singleId> ./out --skip-patching
grep -qi BEST-EFFORT ./out/XS_SingleServer/query.sql && echo "WRONG: fell back" || echo "OK: real combined SQL"
docker exec -i rdmp-mssql  /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$PW" -C -d TEST_ScratchArea -h -1 -W \
  -Q "$(cat ./out/XS_SingleServer/query.sql)"                    # -> P01 P02 P05 P07
```

## 6. Teardown

```bash
docker rm -f rdmp-mssql2                                          # remove the 2nd server + its data
# optional: drop the server-1 fixture tables / catalogues
docker exec -i rdmp-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$PW" -C -d TEST_ScratchArea \
  -Q "DROP TABLE IF EXISTS dbo.XS_S1a; DROP TABLE IF EXISTS dbo.XS_S1b;"
# (delete the XS_* Catalogues/CICs from the GUI or with `rdmp cmd Delete ...` if you want a clean platform)
```

## Notes
- `rdmp cmd ListSupportedCommands` returns **nothing** if `Databases.yaml` points at an unreachable
  server (e.g. the neutral `(localdb)\MSSQLLocalDB` default on macOS) — it needs a live platform
  connection to list commands. Point it at docker (`localhost,1433`) to confirm discovery.
- The platform DB can get wiped between sessions (docker re-install / volume reset) — rebuild the
  fixtures from this doc if `list Catalogue` comes back empty.
