Write-Output "Running script..."

cd "..\bin\Release\PPMCheckerTool\win-x64\publish\"

Write-Output "Current Power mode"
& "$PSScriptRoot\PowerMode.exe"

#####################
# RECOMMENDED
#####################
Write-Output "Changing Power mode to Default(Recommended) (00000000-0000-0000-0000-000000000000)"
& "$PSScriptRoot\PowerMode.exe" 00000000-0000-0000-0000-000000000000

wpr -start $PSScriptRoot\power.wprp
Start-Sleep -Seconds 0.5
wpr -stop "$PSScriptRoot\power_rec.etl"

Write-Output "Running PPM Checker for Default Power scheme..."

& ".\PPMCheckerTool.exe" -i $PSScriptRoot\power_rec.etl -o $PSScriptRoot\output_rec.csv

Write-Output "Default(Recommended) power scheme done..."

#####################
# BETTER BATTERY
#####################
Write-Output "Changing Power mode to Better Battery"
& "$PSScriptRoot\PowerMode.exe" BetterBattery

wpr -start $PSScriptRoot\power.wprp
Start-Sleep -Seconds 0.5
wpr -stop "$PSScriptRoot\power_betterbattery.etl"

Write-Output "Running PPM Checker for Better Battery Power scheme..."

& ".\PPMCheckerTool.exe" -i $PSScriptRoot\power_betterbattery.etl -o $PSScriptRoot\output_betterbattery.csv

Write-Output "Better Battery power scheme done..."

#####################
# BETTER PERFORMANCE
#####################
Write-Output "Changing Power mode to Better Performance"
& "$PSScriptRoot\PowerMode.exe" BetterPerformance

wpr -start $PSScriptRoot\power.wprp
Start-Sleep -Seconds 0.5
wpr -stop "$PSScriptRoot\power_betterperf.etl"

Write-Output "Running PPM Checker for Better Performance Power scheme..."

& ".\PPMCheckerTool.exe" -i $PSScriptRoot\power_betterperf.etl -o $PSScriptRoot\output_betterperf.csv

Write-Output "Better Performance power scheme done..."

#####################
# BEST PERFORMANCE
#####################
Write-Output "Changing Power mode to Best Performance"
& "$PSScriptRoot\PowerMode.exe" BestPerformance

wpr -start $PSScriptRoot\power.wprp
Start-Sleep -Seconds 0.5
wpr -stop "$PSScriptRoot\power_bestperf.etl"

Write-Output "Running PPM Checker for Best Performance Power scheme..."

& ".\PPMCheckerTool.exe" -i $PSScriptRoot\power_bestperf.etl -o $PSScriptRoot\output_bestperf.csv

Write-Output "Best Performance power scheme done..."