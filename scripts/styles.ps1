# SGR style matrix — run inside Velo.
$e = [char]27
Write-Host "$e[1mbold$e[0m $e[3mitalic$e[0m $e[2mdim$e[0m $e[7minverse$e[0m $e[9mstrike$e[0m"
Write-Host "$e[4msingle$e[0m $e[21mdouble$e[0m $e[4:3mcurly$e[0m $e[4:4mdotted$e[0m $e[4:5mdashed$e[0m"
Write-Host "$e[4m$e[58;2;255;80;80mred underline$e[0m $e[38;2;255;165;0m24-bit orange$e[0m $e[48;2;40;40;120m24-bit bg$e[0m"
