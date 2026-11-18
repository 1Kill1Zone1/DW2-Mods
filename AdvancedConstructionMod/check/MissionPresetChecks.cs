using DistantWorlds.Types;
using DistantWorlds.UI;
using DistantWorlds2;
using HarmonyLib;

internal static partial class Checks
{
    private static void TestMissionPresetEditor(ShipDesignRenderer renderer,Galaxy galaxy,Empire empire,Design source,Action<DWControl> click)
    {
        var type=AccessTools.TypeByName("DW2CoherentResearch.DesignerMissionPanel");
        AccessTools.Method(type,"Build").Invoke(null,[renderer]);
        var panel=(DWPanel)UserInterfaceController.CustomControls["MissionPanel"];
        var edit=panel.Controls.OfType<DWButton>().Single(b=>b.Name=="Coherent.EditMissionPreset");
        DWPanel Dialog()=>(DWPanel)UserInterfaceController.CustomControls["Coherent.MissionPreset"];
        void Submit(string text)=>click(Dialog().Controls.OfType<DWButton>().Single(b=>b.Text==text));
        DWTextBox Share()=>Dialog().Controls.OfType<DWTextBox>().Single(b=>b.Name=="Point-defense bays (%)");
        void Choose(DWButton button,string text)
        {
            click(button);
            var row=Dialog().Controls.OfType<DWButton>().Single(b=>b.Name.StartsWith("Preset.Choice.") && b.Visible && b.Text==text);
            click(row);
            Require(button.Text==text,"native preset dropdown selects "+text);
        }
        void Tick()=>AccessTools.Method(type,"Updated").Invoke(null,[renderer]);
        var original=source.Components;
        var top=(DWButton)renderer.UpgradePanel.Controls.Single(c=>c.Name=="ACM.Specialization");
        Require(!top.Visible,"lower mission selector suppresses duplicate upper selector");

        click(edit);
        Require(Dialog().Controls.OfType<DWTextBox>().Count()==4,"native preset editor exposes four percentage fields");
        Share().Text="50";
        var up=(DWButton)Dialog().Controls.Single(c=>c.Name=="Preset.414.79");
        var down=(DWButton)Dialog().Controls.Single(c=>c.Name=="Preset.414.95");
        click(up);Require(Share().Text=="55","upper arrow increases five points");
        click(down);Require(Share().Text=="50" && down.Position.X==up.Position.X && up.Size.Y<down.Position.Y-up.Position.Y,"lower arrow decreases five points, stacked hit areas");
        Share().Text="101";Submit("Save preset");Require(Dialog().Visible,"invalid percentage leaves preset editor open");
        var reserve=Dialog().Controls.OfType<DWTextBox>().Single(b=>b.Name=="Bays reserved for other weapons (%)");
        var any=(DWButton)Dialog().Controls.Single(c=>c.Name=="Preset.310.227");
        Choose(any,"No");Choose(any,"Yes");reserve.Text="0";Share().Text="100";Tick();
        var pdCount=(DWLabel)Dialog().Controls.Single(c=>c.Name=="Preset.Label.232.79");
        Require(pdCount.Text=="(4 of 4)","live four-turret frigate forecast before Apply: "+pdCount.Text);
        reserve.Text="50";Tick();
        Require(pdCount.Text!="(4 of 4)" && ReferenceEquals(original,source.Components),"reserve change updates live forecast and preserves source");
        Share().Text="65";Submit("Cancel");click(edit);Require(Share().Text!="65","cancel discards unsaved changes");
        var title=Dialog().HeaderText;
        Share().Text="65";
        var stance=(DWButton)Dialog().Controls.Single(c=>c.Name=="Preset.310.190");
        Choose(stance,"Automatic (no override)");
        Choose(stance,"Neutral");
        Choose(stance,"Aggressive");
        var family=(DWButton)Dialog().Controls.Single(c=>c.Name=="Preset.310.301");
        string beam=empire.Research.ResearchedComponents.First(c=>c.Definition.Name.StartsWith("Particle Beam")).Definition.Family;
        Choose(family,beam);
        Require(family.Text==beam,"researched beam family can be selected");
        Dialog().Controls.OfType<DWTextBox>().Single(b=>b.Name=="Preferred range (%)").Text="20";
        Submit("Apply preview");
        var draft=(Design)AccessTools.Field(renderer.GetType(),"_Design").GetValue(renderer)!;
        Require(draft.Tactics.AttackRangeNormal==TacticsStance.Aggressive && draft.Tactics.AttackRangeStronger==TacticsStance.Aggressive,
            "preset sets both native combat stances on the draft");
        Require(draft.Components.Any(c=>!c.IsEmpty && c.Component.Family==beam),"preferred researched beam installed on draft");

        Require(!UserInterfaceController.CustomControls.ContainsKey("Coherent.MissionPreset") && ReferenceEquals(original,source.Components),
            "preset preview closes and leaves published components intact");
        click(edit);Require(Dialog().HeaderText==title && Share().Text=="65","preview draft can be reopened before saving preset");
        Submit("Save preset");
        click(edit);Require(Share().Text=="65" && Dialog().Controls.OfType<DWButton>().Any(b=>b.Text==beam),"campaign preset persists percent and family on reopening");
        var role=empire.InferShipRole(source.GetShipHull(galaxy));
        var names=new[]{"Long","Picket","Medium","Close","Carrier"};
        var entries=names.Select(name=>getSaved(galaxy,"coherent-research",$"preset.v1.{empire.EmpireId}.{role}.{name}")).ToArray();
        Require(entries.Count(v=>v?.Contains("0.65")==true)==1,"exactly one role and mission override written to Matrix");
        Require(names.All(name=>string.IsNullOrEmpty(getSaved(galaxy,"coherent-research",$"preset.v1.{empire.EmpireId}.Escort.{name}"))),"frigate edit leaves escort rules intact");
        var hull=source.GetShipHull(galaxy);
        DWButton ScopeButton()=>Dialog().Controls.OfType<DWButton>().Single(b=>b.Name=="Preset.310.340");
        void ChangeScope(string scope)=>Choose(ScopeButton(),scope);
        string classScope=$"Class: {role}",hullScope=$"Hull: {hull.Name}";
        ChangeScope("All classes");Share().Text="30";Submit("Save preset");click(edit);
        Require(Share().Text=="65","legacy class rule wins over global");
        ChangeScope(hullScope);Require(Share().Text=="65","clean hull inherits class");
        Share().Text="75";Submit("Save preset");click(edit);
        Require(ScopeButton().Text==hullScope && Share().Text=="75","reopened hull displays its saved rule");
        var mission=names[Array.FindIndex(entries,v=>v?.Contains("0.65")==true)];
        var presets=AccessTools.TypeByName("DW2CoherentResearch.MissionPresets");
        var definition=AccessTools.Method(AccessTools.TypeByName("DW2CoherentResearch.ShipMissions"),"Definition").Invoke(null,[mission]);
        float Resolved(ShipRole testedRole,ShipHull testedHull)
        {
            var result=AccessTools.Method(presets,"Resolve").Invoke(null,[empire,testedRole,definition,testedHull])!;
            return (float)AccessTools.Field(result.GetType(),"PointDefenseShare").GetValue(result)!;
        }
        var sibling=new ShipHull{ShipHullId=unchecked((short)(hull.ShipHullId+1))};
        Require(Resolved(role,hull)==.75f && Resolved(role,sibling)==.65f && Resolved(ShipRole.Escort,sibling)==.30f,"hull, sibling and another class resolve independently");
        Submit("Inherit");click(edit);
        Require(Resolved(role,hull)==.65f && ScopeButton().Text==classScope,"clearing hull reveals class");
        Submit("Inherit");click(edit);
        Require(Share().Text=="30" && Resolved(role,hull)==.30f,"clearing class reveals global");
        ChangeScope("All classes");Submit("Inherit");click(edit);
        Require(!Dialog().Controls.OfType<DWButton>().Single(b=>b.Text=="Inherit").Enabled && Resolved(role,hull)==(float)AccessTools.Field(definition!.GetType(),"PointDefenseShare").GetValue(definition)!,"clearing last rule restores Coherent default");
        Submit("Cancel");panel.Visible=false;UserInterfaceController.RemoveCustomControl(panel);
        DW2ModLauncher.Loader.LiveChannels.Channels.TryGetValue("advanced-construction.refresh-editor-mission",out var refresh);
        ((Action<object>)refresh!)(renderer);
    }
}
