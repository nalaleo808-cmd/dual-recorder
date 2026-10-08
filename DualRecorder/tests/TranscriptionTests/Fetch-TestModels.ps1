param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference = 'Stop'
$Destination = [System.IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$asr = 'https://huggingface.co/csukuangfj/sherpa-onnx-streaming-zipformer-en-kroko-2025-08-06/resolve/572aaf4e2e0c603c3fc2a574d096e755a178faa1/'
$fixture = 'https://huggingface.co/csukuangfj/sherpa-onnx-streaming-zipformer-en-2023-06-26/resolve/672fbf1b30579d6585301139bb363f42a0ad4a24/'
$files = @(
 @('encoder.onnx', ($asr + 'encoder.onnx')),
 @('decoder.onnx', ($asr + 'decoder.onnx')),
 @('joiner.onnx', ($asr + 'joiner.onnx')),
 @('tokens.txt', ($asr + 'tokens.txt')),
 @('segmentation.onnx', 'https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/9403a6902bb58e3d5ae8c7e77c3422de279db2e0/model.onnx'),
 @('embedding.onnx', 'https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/wespeaker_en_voxceleb_resnet34_LM.onnx'),
 @('test-asr.wav', ($fixture + 'test_wavs/0.wav')),
 @('test-speakers.wav', 'https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/1-two-speakers-en.wav')
)
foreach ($file in $files) { Invoke-WebRequest -Uri $file[1] -OutFile (Join-Path $Destination $file[0]) }
Write-Output 'Public speech models and fixtures downloaded. Tests verify the pinned model hashes before loading them.'
