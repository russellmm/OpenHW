#!/bin/bash
# run.sh <burn binary> <out dir> [load s] [cool s]: does each BMC PCIe slot sensor follow the GPU in that slot?
# 2 min idle, then each GPU (PCI bus order) loaded at its current power limit, then left to cool; log.py samples
# nvidia-smi and the helper's `bmc` reply every 5 s into <out dir>/slots.csv (phases in phases.txt).
# Build the load: nvcc -O2 -o <scratch>/burn burn.cu. Result 2026-11-17: PCIE04 = RTX 5060 Ti (slot 4), PCIE05 = PRO 6000
# at C1 (slot 5), PCIE01 = PRO 6000 at F1 (slot 1); each slot sensor read its GPU's core temperature within ~1 °C.
set -u; B=$1; O=$2; LOAD=${3:-300}; COOL=${4:-150}; export CUDA_DEVICE_ORDER=PCI_BUS_ID
n=$(nvidia-smi -L | wc -l)
python3 "$(dirname "$0")/log.py" "$O/slots.csv" $(( 120 + n * (LOAD + COOL) + 30 )) & L=$!
: > "$O/phases.txt"; t0=$(date +%s); sleep 120
for i in $(seq 0 $((n - 1))); do
  echo "gpu$i load $(( $(date +%s) - t0 ))" >> "$O/phases.txt"; "$B" $i "$LOAD"
  echo "gpu$i cool $(( $(date +%s) - t0 ))" >> "$O/phases.txt"; sleep "$COOL"
done
echo "end $(( $(date +%s) - t0 ))" >> "$O/phases.txt"; kill $L
