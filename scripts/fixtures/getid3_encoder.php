<?php
// Prints, for each file named on the command line, the encoder and encoder options getID3 reports
// (module.audio.mp3.php: the LAME version, recovered from the padding where the tag cuts it short,
// and GuessEncoderOptions), as one JSON object per line. Uses the getID3 in third_party/getid3.
require_once __DIR__ . '/../../third_party/getid3/getid3/getid3.php';

$getid3 = new getID3();
foreach (array_slice($argv, 1) as $path) {
    $info = $getid3->analyze($path);
    echo json_encode([
        'path' => $path,
        'encoder' => $info['audio']['encoder'] ?? null,
        'encoder_options' => $info['audio']['encoder_options'] ?? null,
        'error' => $info['error'] ?? null,
    ], JSON_UNESCAPED_SLASHES) . "\n";
}
