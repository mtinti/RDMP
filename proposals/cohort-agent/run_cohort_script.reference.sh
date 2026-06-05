#!/usr/bin/env bash
# RUNNER: replay an exported build.script.yaml to rebuild the cohort under a new name.
# Binds $c/$a/$p handles to the real ids RDMP assigns as each object is created.
#   run_cohort_script.sh <build.script.yaml> <new cohort name>
set -euo pipefail
cd "$(dirname "$0")/.."
DLL=Tools/rdmp/bin/Release/net10.0/rdmp.dll; SA='YourStrong!Passw0rd'
SCRIPT="$1"; NEWNAME="$2"
q(){ docker exec rdmp-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SA" -C -h -1 -W -Q "SET NOCOUNT ON; $1" 2>/dev/null | tr -d '[:space:]'; }
maxid(){ q "SELECT ISNULL(MAX(ID),0) FROM TEST_Catalogue.dbo.$1"; }
HKEYS=(); HVALS=(); RECS=()                          # handle->id ; and parent/kind/child add-order
seth(){ HKEYS+=("$1"); HVALS+=("$2"); }              # bind handle (no $) to id
subst(){ # substitute all known handles in $1 (longest key first)
  local s="$1" i order
  order=$(for i in "${!HKEYS[@]}"; do echo "${#HKEYS[$i]} $i"; done | sort -rn | cut -d' ' -f2)
  for i in $order; do s="${s//\$${HKEYS[$i]}/${HVALS[$i]}}"; done
  printf '%s' "$s"
}

while IFS= read -r line; do
  [[ "$line" =~ ^[[:space:]]*-[[:space:]] ]] || continue
  cmd="${line#*- }"; cmd="${cmd%%   #*}"
  binds=""
  if [[ "$cmd" == *" => "* ]]; then binds="${cmd##* => }"; cmd="${cmd% => *}"; fi
  [[ "$cmd" == CreateNewCohortIdentificationConfiguration* ]] && cmd="CreateNewCohortIdentificationConfiguration \"$NEWNAME\""
  cmd="$(subst "$cmd")"
  eval "arr=($cmd)"
  dotnet "$DLL" cmd "${arr[@]}" >/dev/null 2>&1 || { echo "FAILED: $cmd"; exit 1; }
  case "$cmd" in
    CreateNewCohortIdentificationConfiguration*)
      cic=$(q "SELECT ID FROM TEST_Catalogue.dbo.CohortIdentificationConfiguration WHERE Name='$NEWNAME'")
      root=$(q "SELECT RootCohortAggregateContainer_ID FROM TEST_Catalogue.dbo.CohortIdentificationConfiguration WHERE ID=$cic")
      [ -n "$binds" ] && seth "${binds#\$}" "$root"
      for nm in 'Inclusion Criteria' 'Exclusion Criteria'; do
        cid=$(q "SELECT c.ID FROM TEST_Catalogue.dbo.CohortAggregateContainer c JOIN TEST_Catalogue.dbo.CohortAggregateSubContainer s ON s.CohortAggregateContainer_ChildID=c.ID WHERE s.CohortAggregateContainer_ParentID=$root AND c.Name='$nm'")
        [ -n "$cid" ] && dotnet "$DLL" cmd Delete "CohortAggregateContainer:$cid" >/dev/null 2>&1
      done ;;
    AddCohortSubContainer*)
      nid=$(maxid CohortAggregateContainer); [ -n "$binds" ] && seth "${binds#\$}" "$nid"
      pid=$(printf '%s' "$cmd" | grep -oE 'CohortAggregateContainer:[0-9]+' | head -1 | cut -d: -f2)
      RECS+=("$pid SUB $nid") ;;
    AddCatalogueToCohortIdentificationSetContainer*)
      nid=$(maxid AggregateConfiguration); [ -n "$binds" ] && seth "${binds#\$}" "$nid"
      pid=$(printf '%s' "$cmd" | grep -oE 'CohortAggregateContainer:[0-9]+' | head -1 | cut -d: -f2)
      RECS+=("$pid AGG $nid") ;;
    CreateNewFilter*) for b in $binds; do seth "${b#\$}" "$(maxid AggregateFilterParameter)"; done ;;
  esac
  echo "ok: ${cmd:0:72}"
done < "$SCRIPT"

# Replaying reverses child order within a container (each Add inserts at the top), so set each
# container's child Order to the script add-sequence -> preserves UNION/INTERSECT/EXCEPT semantics.
for pid in $(printf '%s\n' "${RECS[@]}" | awk '{print $1}' | sort -u); do
  n=0
  for r in "${RECS[@]}"; do
    set -- $r
    [ "$1" = "$pid" ] || continue
    if [ "$2" = "AGG" ]; then
      q "UPDATE TEST_Catalogue.dbo.CohortAggregateContainer_AggregateConfiguration SET [Order]=$n WHERE CohortAggregateContainer_ID=$pid AND AggregateConfiguration_ID=$3" >/dev/null
    else
      q "UPDATE TEST_Catalogue.dbo.CohortAggregateContainer SET [Order]=$n WHERE ID=$3" >/dev/null
    fi
    n=$((n+1))
  done
done
echo "COPY '$NEWNAME' = CIC $(q "SELECT ID FROM TEST_Catalogue.dbo.CohortIdentificationConfiguration WHERE Name='$NEWNAME'")"
