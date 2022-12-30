[Cmdletbinding()]
Param(
    [Parameter(Mandatory = $true)]
    [string] $ExeLocation,

    [Parameter(Mandatory = $true)]
    [string] $OutputFilePath
)

process
{


    Write-Output "Running script..."

    try
    {
        cd $ExeLocation -ErrorAction Stop
    }
    catch [Exception]{
        Write-Error $_
        exit;
    }

    Write-Output "Current Power mode"
    & ".\SetSliderPowerMode.exe"

    #####################
    # BALANCED/RECOMMENDED
    #####################
    Write-Output "Changing Power mode to  Balanced (Non-Surface) / Recommended(Surface)"
    & ".\SetSliderPowerMode.exe" Default

    Write-Output "Taking ETW trace..."
    try
    {
        wpr -start $PSScriptRoot\power.wprp
        Start-Sleep -Seconds 0.5
        wpr -stop "$PSScriptRoot\power_default.etl"
    }catch [Exception]{
        Write-Error $_
        exit
    }

    Write-Output "Running PPM Checker for Default Power scheme..."
    
    try
    {
        & ".\PPMCheckerTool.exe" -i $PSScriptRoot\power_default.etl -o $OutputFilePath
    }catch [Exception]{
        Write-Error $_
        exit;
    }

    Write-Output "Balanced(Recommended) power scheme done..."

    #####################
    # BETTER BATTERY
    #####################
    Write-Output "Changing Power mode to Better Battery"
    & ".\SetSliderPowerMode.exe" BetterBattery

    Write-Output "Taking ETW trace..."
    try
    {
        wpr -start $PSScriptRoot\power.wprp
        Start-Sleep -Seconds 0.5
        wpr -stop "$PSScriptRoot\power_betterbattery.etl"
    }catch [Exception]{
        Write-Error $_
        exit;
    }

    Write-Output "Running PPM Checker for Better Battery Power scheme..."

    try
    {
        & ".\PPMCheckerTool.exe" -i $PSScriptRoot\power_betterbattery.etl -o $OutputFilePath
    }catch [Exception]{
        Write-Error $_
        exit;
    }
    Write-Output "Better Battery power scheme done..."

    #####################
    # BETTER PERFORMANCE
    #####################
    Write-Output "Changing Power mode to Better Performance"
    & ".\SetSliderPowerMode.exe" BetterPerformance

    Write-Output "Taking ETW trace..."
    try
    {
        wpr -start $PSScriptRoot\power.wprp
        Start-Sleep -Seconds 0.5
        wpr -stop "$PSScriptRoot\power_betterperf.etl"
    }catch [Exception]{
        Write-Error $_
        exit;
    }

    Write-Output "Running PPM Checker for Better Performance Power scheme..."

    try
    {
        & ".\PPMCheckerTool.exe" -i $PSScriptRoot\power_betterperf.etl -o $OutputFilePath
    }catch [Exception]{
        Write-Error $_
        exit;
    }

    Write-Output "Better Performance power scheme done..."

    #####################
    # BEST PERFORMANCE
    #####################
    Write-Output "Changing Power mode to Best Performance"
    & ".\SetSliderPowerMode.exe" BestPerformance

    Write-Output "Taking ETW trace..."
    try
    {
        wpr -start $PSScriptRoot\power.wprp
        Start-Sleep -Seconds 0.5
        wpr -stop "$PSScriptRoot\power_bestperf.etl"
    }catch [Exception]{
        Write-Error $_
        exit;
    }

    Write-Output "Running PPM Checker for Best Performance Power scheme..."

    try
    {
        & ".\PPMCheckerTool.exe" -i $PSScriptRoot\power_bestperf.etl -o $OutputFilePath
    }catch [Exception]{
        Write-Error $_
        exit;
    }

    Write-Output "Best Performance power scheme done..."

    #####################

    Write-Output "Restoring power mode to Balanced"
    Write-Output "Changing Power mode to Balanced (Non-Surface) / Recommended(Surface)"
    & ".\SetSliderPowerMode.exe" Default

    try
    {
        cd $PSScriptRoot
    }
    catch [Exception]{
        Write-Error $_
        exit;
    }
}