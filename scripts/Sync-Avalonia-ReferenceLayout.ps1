param([string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent))

$ErrorActionPreference = 'Stop'
$x = [System.Xml.Linq.XNamespace]'http://schemas.microsoft.com/winfx/2006/xaml'
$ui = [System.Xml.Linq.XNamespace]'https://github.com/avaloniaui'
$document = [System.Xml.Linq.XDocument]::Load((Join-Path $RepositoryRoot 'src/Yita.App/Windows/SettingsWindow.xaml'))
$root = $document.Root
$root.Element($root.Name.Namespace + 'Window.Resources').Remove()
$classes = @{
    CardStyle = 'card'; SectionTitleStyle = 'section'; FieldLabelStyle = 'label'
    SupportingTextStyle = 'supporting'; SecondaryButtonStyle = 'secondary'
    PrimaryButtonStyle = 'primary'; SwitchStyle = 'switch'; FontSizeSliderStyle = 'font-size'
}
$removeAttributes = @('ClassModifier', 'SnapsToDevicePixels', 'TextFormattingMode', 'ResizeMode',
    'PanningMode', 'SelectedValuePath', 'IsEditable', 'SelectionOpacity', 'Checked', 'Unchecked')
foreach ($node in @($root.DescendantsAndSelf())) {
    if ($node.Name.LocalName -eq 'LiquidGlassRim') { $node.Remove(); continue }
    $node.Name = $ui + $node.Name.LocalName
    foreach ($attribute in @($node.Attributes())) {
        if ($attribute.IsNamespaceDeclaration) { $attribute.Remove(); continue }
        $name = $attribute.Name.LocalName
        if ($name -in $removeAttributes -or $name -eq 'TextOptions.TextFormattingMode') { $attribute.Remove(); continue }
        if ($name -eq 'AutomationProperties.Name') {
            $value = $attribute.Value
            $attribute.Remove()
            $node.SetAttributeValue('AutomationProperties.Name', $value)
        }
        elseif ($name -eq 'ToolTip') {
            $value = $attribute.Value
            $attribute.Remove()
            $node.SetAttributeValue('ToolTip.Tip', $value)
        }
        elseif ($name -eq 'VerticalScrollBarVisibility' -and $node.Name.LocalName -eq 'TextBox') {
            $value = $attribute.Value
            $attribute.Remove()
            $node.SetAttributeValue('ScrollViewer.VerticalScrollBarVisibility', $value)
        }
        elseif ($name -eq 'Style') {
            $key = $attribute.Value.Replace('{StaticResource ', '').TrimEnd('}')
            if (-not $classes.ContainsKey($key)) { throw "Unknown reference style: $key" }
            $node.SetAttributeValue('Classes', $classes[$key])
            $attribute.Remove()
        }
        elseif ($name -eq 'Visibility') {
            $node.SetAttributeValue('IsVisible', $(if ($attribute.Value -eq 'Collapsed') { 'False' } else { 'True' }))
            $attribute.Remove()
        }
        elseif ($name -eq 'Name' -and $attribute.Name.NamespaceName -ne $x.NamespaceName) {
            $attribute.Remove()
        }
        elseif ($name -eq 'Text' -and $node.Name.LocalName -eq 'ModelComboBox') { $attribute.Remove() }
        elseif ($name -eq 'InitialShowDelay') { $attribute.Remove() }
        elseif ($name -eq 'Text' -and $node.Name.LocalName -eq 'TextBox') { }
    }
    if ($node.Name.LocalName -eq 'PasswordBox') {
        $node.Name = $ui + 'TextBox'
        $node.SetAttributeValue('PasswordChar', [char]0x25cf)
    }
    if ($node.Attribute('Classes') -and $node.Attribute('Classes').Value -eq 'switch') {
        $node.Name = $ui + 'ToggleSwitch'
    }
    if ($node.Attribute($x + 'Name') -and $node.Attribute($x + 'Name').Value -eq 'ModelComboBox') {
        $node.Name = $ui + 'AutoCompleteBox'
        $node.RemoveNodes()
    }
    if ($node.Name.LocalName -eq 'Image') {
        $node.SetAttributeValue('Source', 'avares://Yita.Desktop/Assets/AppLogo.png')
    }
    if ($node.Name.LocalName -eq 'TextBlock' -and -not $node.Attribute('LineHeight')) {
        $fontSize = if ($node.Attribute('FontSize')) { [double]$node.Attribute('FontSize').Value } else { 14 }
        $lineHeight = if ($fontSize -eq 24) { 31 } else { 20 }
        if ($node.Attribute('Classes') -and $node.Attribute('Classes').Value -eq 'label') { $lineHeight = 19 }
        $node.SetAttributeValue('LineHeight', $lineHeight)
    }
    if ($node.Name.LocalName -eq 'Button' -and $node.Attribute('IsCancel')) {
        $node.SetAttributeValue('Click', 'CancelButton_Click')
    }
}
$root.SetAttributeValue('xmlns', $ui.NamespaceName)
$root.SetAttributeValue([System.Xml.Linq.XNamespace]::Xmlns + 'x', $x.NamespaceName)
$root.SetAttributeValue($x + 'Class', 'Yita.Desktop.MainWindow')
$root.SetAttributeValue('Icon', 'avares://Yita.Desktop/Assets/Yita.ico')
$root.SetAttributeValue('CanResize', 'True')
$root.SetAttributeValue('RenderOptions.TextRenderingMode', 'Antialias')
$root.SetAttributeValue($x + 'CompileBindings', 'False')

$scroll = $root.Descendants() | Where-Object { $_.Attribute($x + 'Name') -and $_.Attribute($x + 'Name').Value -eq 'SettingsScrollViewer' }
$source = $scroll.Element($ui + 'StackPanel')
$cards = @($source.Elements())
if ($cards.Count -ne 4) { throw 'Expected four original settings cards.' }
$shell = [System.Xml.Linq.XElement]::new($ui + 'Grid')
$shell.SetAttributeValue('ColumnDefinitions', '178,*')
$shell.SetAttributeValue('MinWidth', '680')
$navigation = [System.Xml.Linq.XElement]::Parse(@'
<Border xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Margin="0,0,18,0" Padding="7" Background="{DynamicResource AppCardBackgroundBrush}" CornerRadius="16" VerticalAlignment="Top">
  <ListBox x:Name="SettingsNavigation" Classes="settings-navigation" SelectionChanged="SettingsNavigationChanged" SelectedIndex="0">
    <ListBoxItem Tag="general" Content="General" />
    <ListBoxItem Tag="ai-history" Content="AI history" />
    <ListBoxItem Tag="translation-appearance" Content="Translation &amp; appearance" />
    <ListBoxItem Tag="model" Content="Model configuration" />
  </ListBox>
</Border>
'@)
$shell.Add($navigation)
$pages = [System.Xml.Linq.XElement]::new($ui + 'Grid')
$pages.SetAttributeValue('Grid.Column', '1')
$names = @('GeneralPage', 'HistoryPage', 'AppearancePage', 'ModelPage')
for ($index = 0; $index -lt $cards.Count; $index++) {
    $page = [System.Xml.Linq.XElement]::new($ui + 'StackPanel')
    $page.SetAttributeValue($x + 'Name', $names[$index])
    $page.SetAttributeValue('Margin', '0,0,0,10')
    $page.SetAttributeValue('IsVisible', $(if ($index -eq 0) { 'True' } else { 'False' }))
    $cards[$index].Remove()
    $page.Add($cards[$index])
    $pages.Add($page)
}
$shell.Add($pages)
$source.Remove()
$scroll.Add($shell)
$modelField = $root.Descendants($ui + 'AutoCompleteBox') | Where-Object { $_.Attribute($x + 'Name').Value -eq 'ModelComboBox' }
$modelContainer = [System.Xml.Linq.XElement]::Parse(@'
<Grid xmlns="https://github.com/avaloniaui">
  <Button Width="32" Height="38" HorizontalAlignment="Right" Classes="model-arrow" Click="ModelPickerArrowClick">
    <Path Width="8" Height="4" Stroke="{DynamicResource AppMutedTextBrush}" StrokeThickness="1.2" Data="M0,0 L4,4 L8,0" />
  </Button>
</Grid>
'@)
$modelField.ReplaceWith($modelContainer)
$modelContainer.AddFirst($modelField)
foreach ($node in $root.Descendants()) {
    foreach ($attribute in @($node.Attributes())) {
        if ($attribute.IsNamespaceDeclaration) { $attribute.Remove() }
    }
}
$target = Join-Path $RepositoryRoot 'src/Yita.Desktop/MainWindow.axaml'
$document.Save($target)

# The translated text table stays identical to the reference source.
$code = [IO.File]::ReadAllText((Join-Path $RepositoryRoot 'src/Yita.App/Windows/SettingsWindow.xaml.cs'))
$start = $code.IndexOf('    private static readonly IReadOnlyDictionary')
$end = $code.IndexOf('    private string _uiLanguage', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'Reference localization table not found.' }
$table = $code.Substring($start, $end - $start).Replace('private static readonly', 'internal static readonly')
$localization = "namespace Yita.Desktop;`n`ninternal static class SettingsText`n{`n$table}`n"
[IO.File]::WriteAllText((Join-Path $RepositoryRoot 'src/Yita.Desktop/SettingsText.cs'), $localization, [Text.UTF8Encoding]::new($false))
