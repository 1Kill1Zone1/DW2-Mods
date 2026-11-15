using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DistantWorlds.Types;
using DistantWorlds.UI;
using HarmonyLib;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace AdvancedConstructionMod;

internal static class DesignsSeriesColumn
{
    internal const string FieldName="ACM.Series";
    private static readonly ConditionalWeakTable<DesignsDialog,Layout> Dialogs=new();
    private static readonly ConditionalWeakTable<ListDrawableFieldList,Layout> Layouts=new();
    private static readonly ConditionalWeakTable<DWButtonStrip,Layout> Headers=new();
    private static readonly FieldInfo ListFields=AccessTools.Field(typeof(ScrollableList),"_Fields");
    private static readonly FieldInfo SortField=AccessTools.Field(typeof(DesignsDialog),"_SortField"),SortDirection=AccessTools.Field(typeof(DesignsDialog),"_SortDirection");
    private static readonly MethodInfo SortClick=AccessTools.Method(typeof(DesignsDialog),"SortDesignsColumnClick");
    private static readonly MethodInfo BindList=AccessTools.Method(typeof(DesignsDialog),"BindDesignsList");
    private static readonly MethodInfo DrawText=AccessTools.Method(typeof(DrawingHelper),nameof(DrawingHelper.DrawStringDropShadow),
        [typeof(SpriteBatch),typeof(string),typeof(SpriteFont),typeof(Color),typeof(Color),typeof(Vector2)]);

    internal static void Install(Harmony harmony)
    {
        foreach(string method in new[]{"Initialize","BindDesignsList"})
            harmony.Patch(AccessTools.DeclaredMethod(typeof(DesignsDialog),method),
                postfix:new HarmonyMethod(typeof(DesignsSeriesColumn),nameof(Attach)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(DesignsDialog),"BindDesignsList"),
            transpiler:new HarmonyMethod(typeof(DesignsSeriesColumn),nameof(SortBoundRows)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(DWButtonStrip),nameof(DWButtonStrip.DoLayout)),
            postfix:new HarmonyMethod(typeof(DesignsSeriesColumn),nameof(AfterHeaderLayout)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(DesignsDialog),"Update"),postfix:new HarmonyMethod(typeof(DesignsSeriesColumn),nameof(AfterSelection)));
        harmony.Patch(AccessTools.DeclaredMethod(typeof(DesignsDialog),"DesignsListItemClicked"),postfix:new HarmonyMethod(typeof(DesignsSeriesColumn),nameof(AfterSelection)));
        var draw=AccessTools.DeclaredMethod(typeof(Design),"DistantWorlds.Types.IListDrawable.DrawItem");
        harmony.Patch(draw,prefix:new HarmonyMethod(typeof(DesignsSeriesColumn),nameof(BeforeDraw)),
            transpiler:new HarmonyMethod(typeof(DesignsSeriesColumn),nameof(ClipName)));
    }

    internal static void Attach(DesignsDialog __instance)
    {
        var list=__instance.DesignsList;var headings=__instance.ColumnHeadings;
        if(list==null||headings==null)return;
        if(__instance.Empire?.IsPlayer==true&&__instance.Galaxy is { } ownerGalaxy)SeriesAutomation.EnsureModes(ownerGalaxy,__instance.Empire,out _,out _);
        if(!Dialogs.TryGetValue(__instance,out var layout))
        {
            layout=new Layout(__instance);Dialogs.Add(__instance,layout);
            Layouts.Add(layout.HitFields,layout);Layouts.Add(layout.NativeFields,layout);
        }
        ListFields.SetValue(list,layout.HitFields);
        Headers.Remove(headings);Headers.Add(headings,layout);
        layout.ArrangeHeader(headings);
        layout.ArrangeDetails();
        if((string?)SortField.GetValue(__instance)==FieldName)
            headings.SetButtonSort(headings.Controls.IndexOf(layout.SeriesHeader!), (bool)SortDirection.GetValue(__instance)!);
    }
    private static void AfterHeaderLayout(DWButtonStrip __instance)
    {if(Headers.TryGetValue(__instance,out var layout))layout.ArrangeHeader(__instance);}
    private static void AfterSelection(DesignsDialog __instance)
    {if(Dialogs.TryGetValue(__instance,out var layout))layout.RefreshDetails();}

    internal static void RefreshCommittedSeries(Galaxy galaxy,Empire empire)
    {
        foreach(var entry in Dialogs)
        {
            var dialog=entry.Key;
            if(!dialog.Visible||!ReferenceEquals(dialog.Galaxy,galaxy)||!ReferenceEquals(dialog.Empire,empire)||
                (string?)SortField.GetValue(dialog)!=FieldName)continue;
            var selected=dialog.DesignsList.SelectedItems.ToArray();
            BindList.Invoke(dialog,[null]);
            dialog.DesignsList.SelectedItems=selected.Where(item=>dialog.DesignsList.Items.Contains(item)).ToList();
        }
    }

    internal static void BeforeDraw(ref ListDrawableFieldList fields)
    {
        if(Layouts.TryGetValue(fields,out var layout))fields=layout.NativeFields;
    }

    private static IEnumerable<CodeInstruction> SortBoundRows(IEnumerable<CodeInstruction> instructions)
    {
        var code=instructions.ToList();bool replaced=false;
        for(int i=0;i<code.Count;i++)
        {
            if(replaced||code[i].operand is not MethodInfo method||method.Name!="ToArray"||method.ReturnType!=typeof(Design[])||method.GetParameters().Length!=0)continue;
            var load=new CodeInstruction(OpCodes.Ldarg_0);load.labels.AddRange(code[i].labels);code[i].labels.Clear();
            code.Insert(i++,load);code[i]=new(OpCodes.Call,AccessTools.Method(typeof(DesignsSeriesColumn),nameof(SortedRows)));replaced=true;
        }
        if(!replaced)throw new InvalidOperationException("Steam Designs binding API did not match the Series sort.");
        return code;
    }

    private static Design[] SortedRows(DesignList designs,DesignsDialog dialog)
    {
        if((string?)SortField.GetValue(dialog)==FieldName&&Dialogs.TryGetValue(dialog,out var layout))
            layout.Sort(designs,(bool)SortDirection.GetValue(dialog)!);
        return designs.ToArray();
    }

    private static IEnumerable<CodeInstruction> ClipName(IEnumerable<CodeInstruction> instructions)
    {
        var code=instructions.ToList();int replaced=0;
        // Native row layout is positional. Only its noncompact Name draw needs a narrower cell.
        for(int i=0;i<code.Count;i++)
        {
            if(!code[i].Calls(DrawText))continue;
            bool name=false;
            for(int j=Math.Max(0,i-10);j<i;j++)
                if(code[j].Calls(AccessTools.PropertyGetter(typeof(Design),nameof(Design.Name))))name=true;
            if(!name)
            {
                var extra=new[]{new CodeInstruction(OpCodes.Ldarg_S,(byte)17),new CodeInstruction(OpCodes.Ldarg_S,(byte)5),new CodeInstruction(OpCodes.Ldarg_S,(byte)21)};
                extra[0].labels.AddRange(code[i].labels);code[i].labels.Clear();
                foreach(var arg in extra)code.Insert(i++,arg);
                code[i]=new(OpCodes.Call,AccessTools.Method(typeof(DesignsSeriesColumn),nameof(DrawOther)));continue;
            }
            var load=new CodeInstruction(OpCodes.Ldarg_S,(byte)17);load.labels.AddRange(code[i].labels);code[i].labels.Clear();
            code.Insert(i++,load);
            foreach(var extra in new[]{new CodeInstruction(OpCodes.Ldarg_0),new CodeInstruction(OpCodes.Ldarg_S,(byte)18),
                new CodeInstruction(OpCodes.Ldarg_S,(byte)19),new CodeInstruction(OpCodes.Ldarg_S,(byte)21)})code.Insert(i++,extra);
            code[i]=new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(DesignsSeriesColumn),nameof(DrawName)));
            replaced++;
        }
        if(replaced!=1)throw new InvalidOperationException("Steam design Name drawing API did not match the Series column.");
        return code;
    }

    private static void DrawName(SpriteBatch batch,string text,SpriteFont font,Color color,Color shadow,Vector2 position,ListDrawableFieldList fields,
        Design design,Galaxy galaxy,Empire empire,ref HoverTipList hoverTips)
    {
        if(!Layouts.TryGetValue(fields,out var layout))
        {DrawingHelper.DrawStringDropShadow(batch,text,font,color,shadow,position);return;}
        DrawCell(batch,text,font,color,shadow,position,S(layout.Name.Size-layout.Name.HorizontalPadding-4));
        // Draw alongside the native Name, including its first layout pass; native ignores considerInView here.
        string series=layout.Text(design,galaxy,empire);
        float x=position.X+S(layout.Name.Size-layout.Name.HorizontalPadding+layout.Series.HorizontalPadding);
        float width=S(layout.Series.Size-layout.Series.HorizontalPadding-4);
        DrawCell(batch,series,font,color,shadow,new(x,position.Y),width);
        hoverTips.Add(RowTip("Name",text,new RectangleF(position.X,position.Y,S(layout.Name.Size-layout.Name.HorizontalPadding-4),font.GetTotalLineSpacing(font.Size)),S(320),font,color,shadow));
        if(series!="—")hoverTips.Add(RowTip("Series",series,new RectangleF(x,position.Y,width,font.GetTotalLineSpacing(font.Size)),S(320),font,color,shadow));
        if(series!="—"&&layout.NativeFields.FirstOrDefault(f=>string.Equals(f.Name,"AutoDesignRole",StringComparison.OrdinalIgnoreCase)) is { } auto)
            hoverTips.Add(RowTip("Series component upgrade",SeriesAutomation.Description(galaxy,empire,design),
                new RectangleF(position.X+S(layout.NativeFields.GetFieldPosition(auto.Name)),position.Y,S(auto.Size),font.GetTotalLineSpacing(font.Size)),S(440),font,color,shadow));
    }

    private static void DrawCell(SpriteBatch batch,string text,SpriteFont font,Color color,Color shadow,Vector2 position,float width)
    {
        DrawingHelper.DrawStringDropShadow(batch,Fit(text,font,width),font,color,shadow,position,
            new Vector2(Math.Max(1,width),font.GetTotalLineSpacing(font.Size)),TextAlignment.Left);
    }
    private static void DrawOther(SpriteBatch batch,string text,SpriteFont font,Color color,Color shadow,Vector2 position,ListDrawableFieldList fields,Vector2 rowPosition,ref HoverTipList tips)
    {
        if(!Layouts.TryGetValue(fields,out var layout)){DrawingHelper.DrawStringDropShadow(batch,text,font,color,shadow,position);return;}
        float relative=(position.X-rowPosition.X)/S(1);
        var field=layout.NativeFields.LastOrDefault(f=>layout.NativeFields.GetFieldPosition(f.Name)<=relative+.01f);
        if(field==null){DrawingHelper.DrawStringDropShadow(batch,text,font,color,shadow,position);return;}
        float width=Math.Max(1,rowPosition.X+S(layout.NativeFields.GetFieldPosition(field.Name)+field.Size)-position.X-S(4));
        string fit=Fit(text,font,width);DrawingHelper.DrawStringDropShadow(batch,fit,font,color,shadow,position);
        if(fit!=text)tips.Add(RowTip(field.Name,text,new RectangleF(position.X,position.Y,width,font.GetTotalLineSpacing(font.Size)),S(400),font,color,shadow));
    }
    // HoverTip's constructor leaves fonts/colors empty; these rows have no owning DWControl to generate them.
    private static HoverTip RowTip(string title,string text,RectangleF rectangle,float width,SpriteFont font,Color color,Color shadow)=>
        new(title,text,rectangle,width,Side.Right)
        {TitleFont=font,TextFont=font,TitleColor=color,TextColor=color,ShadowColor=shadow,BackColor=ColorHelper.GalactopediaPanelBackColor,Margin=S(6)};
    internal static string Fit(string text,SpriteFont font,float width)
    {
        text=text.Replace('\r',' ').Replace('\n',' ').Replace('\t',' ');
        if(width<=0)return string.Empty;
        if(font.MeasureString(text).X<=width)return text;
        const string ellipsis="…";
        if(font.MeasureString(ellipsis).X>width)return string.Empty;
        int low=0,high=text.Length;
        while(low<high)
        {
            int middle=(low+high+1)/2;
            if(font.MeasureString(text[..middle]+ellipsis).X<=width)low=middle;else high=middle-1;
        }
        if(low>0&&char.IsHighSurrogate(text[low-1]))low--;
        return text[..low]+ellipsis;
    }
    private static float S(float value)=>UserInterfaceHelper.CalculateScaledValue(value);

    internal sealed class Layout
    {
        private readonly DesignsDialog dialog;
        internal readonly ListDrawableFieldList NativeFields=new(),HitFields=new();
        internal readonly ListDrawableField Name,Series;
        private DWButton? nameButton;
        private DWButton? heading;
        internal DWButton? NameHeader=>nameButton;
        internal DWButton? SeriesHeader=>heading;
        private DWLabel? details;
        private ScrollableList? detailsList;
        private float fullListHeight,lastListHeight;
        internal DWLabel? Details=>details;

        internal Layout(DesignsDialog dialog)
        {
            this.dialog=dialog;
            NativeFields.StartOffset=HitFields.StartOffset=Design.Fields.StartOffset;
            foreach(var field in Design.Fields)
            {
                NativeFields.Add(new(field.Name,field.Size,field.ShowColumnHeader,field.HorizontalPadding));
                HitFields.Add(new(field.Name,field.Size,field.ShowColumnHeader,field.HorizontalPadding));
            }
            int index=HitFields.FindIndex(f=>string.Equals(f.Name,"Name",StringComparison.OrdinalIgnoreCase));
            if(index<0||index+1>=HitFields.Count||!string.Equals(HitFields[index+1].Name,"Role",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Steam Designs Name/Role columns did not match the Series layout.");
            Name=HitFields[index];Series=new(FieldName,120,true,Name.HorizontalPadding);HitFields.Insert(index+1,Series);
            NativeFields[index].Size=Name.Size+Series.Size;
            foreach(var pair in new[]{("Role",120f),("ShipHull",150f),("DateCreated",80f)})
            {HitFields.Single(f=>f.Name==pair.Item1).Size=pair.Item2;NativeFields.Single(f=>f.Name==pair.Item1).Size=pair.Item2;}
        }

        internal string Text(Design design,Galaxy galaxy,Empire empire)
        {
            if(!Own(design,galaxy,empire))return "—";
            var registry=ConstructionCampaigns.Get(galaxy,empire);
            if(!registry.TryGet(empire,out var state,out _))return "Unavailable";
            return state.DesignClasses.TryGetValue(design.DesignId,out var id)&&state.Classes.TryGetValue(id,out var name)?name:"—";
        }

        private bool Own(Design design,Galaxy galaxy,Empire empire)=>galaxy!=null&&empire!=null&&ReferenceEquals(empire,galaxy.Empires.GetPlayer())&&
            ReferenceEquals(dialog.Galaxy,galaxy)&&ReferenceEquals(dialog.Empire,empire)&&design.EmpireId==empire.EmpireId&&
            ReferenceEquals(empire.Designs.GetById(design.DesignId),design)&&ReferenceEquals(galaxy.Designs.GetById(design.DesignId),design);

        internal void Sort(DesignList designs,bool descending)
        {
            var galaxy=dialog.Galaxy;var empire=dialog.Empire;ConstructionSnapshot? state=null;
            if(galaxy!=null&&empire!=null&&ReferenceEquals(empire,galaxy.Empires.GetPlayer())&&
                ConstructionCampaigns.Get(galaxy,empire).TryGet(empire,out var committed,out _))state=committed;
            string? SeriesName(Design design)=>state!=null&&Own(design,galaxy!,empire!)&&state.DesignClasses.TryGetValue(design.DesignId,out var id)&&
                state.Classes.TryGetValue(id,out var value)?value:null;
            designs.Sort(Comparer<Design>.Create((left,right)=>
            {
                string? a=SeriesName(left),b=SeriesName(right);
                int order=a==null?(b==null?0:1):b==null?-1:StringComparer.OrdinalIgnoreCase.Compare(a,b)*(descending?-1:1);
                if(order==0)order=StringComparer.OrdinalIgnoreCase.Compare(left.Name,right.Name);
                return order!=0?order:left.DesignId.CompareTo(right.DesignId);
            }));
        }

        private void SortSeries(object? sender,DWEventArgs args)
        {
            var selected=dialog.DesignsList.SelectedItems.ToArray();
            SortClick.Invoke(dialog,[sender,args]);
            dialog.DesignsList.SelectedItems=selected.Where(item=>dialog.DesignsList.Items.Contains(item)).ToList();
        }

        internal void ArrangeHeader(DWButtonStrip headings)
        {
            var button=headings.Controls.OfType<DWButton>().FirstOrDefault(b=>b.ExtraClickData is string key&&string.Equals(key,"Name",StringComparison.OrdinalIgnoreCase));
            if(button==null)throw new InvalidOperationException("Steam Designs Name header was not found.");
            nameButton=button;
            float width=Math.Max(1,S(Name.Size));
            button.SetSizeAndPosition(new(width,button.Size.Y),button.Position);
            float origin=button.Position.X-S(HitFields.GetFieldPosition("Name"));
            foreach(var stock in headings.Controls.OfType<DWButton>().Where(b=>b.ExtraClickData is string key&&key!=FieldName))
            {
                var field=HitFields.FirstOrDefault(f=>f.Name==(string)stock.ExtraClickData);
                if(field!=null)stock.SetSizeAndPosition(new(S(field.Size),stock.Size.Y),new(origin+S(HitFields.GetFieldPosition(field.Name)),button.Position.Y));
            }
            Vector2 position=new(origin+S(HitFields.GetFieldPosition(FieldName)),button.Position.Y);
            if(heading==null||!ReferenceEquals(heading.Parent,headings))
            {
                heading=new DWButton{Name="ACM.SeriesHeader",Text="Series",ExtraClickData=FieldName,ClickEvent=SortSeries};
                heading.Initialize(button.FontSizeNormal,button.FontSizeLarge,button.TilingBackground);
                heading.ButtonColor=button.ButtonColor;heading.ButtonColorHover=button.ButtonColorHover;heading.ForeColorHover=button.ForeColorHover;
                heading.ForeColor=button.ForeColor;heading.ShadowColor=button.ShadowColor;heading.BackColor=button.BackColor;
                heading.TextAlignment=button.TextAlignment;heading.UseBoldFont=button.UseBoldFont;heading.Margin=button.Margin;
                heading.ShowTexture=button.ShowTexture;heading.ShowTextureHovered=button.ShowTextureHovered;
                heading.Visible=true;heading.PostScene=button.PostScene;headings.AddControl(heading);
            }
            heading.SetSizeAndPosition(new(S(Series.Size),button.Size.Y),position);
        }
        internal void ArrangeDetails()
        {
            var list=dialog.DesignsList;float height=S(48);
            if(!ReferenceEquals(detailsList,list)||Math.Abs(list.Size.Y-lastListHeight)>.1f){detailsList=list;fullListHeight=list.Size.Y;}
            lastListHeight=Math.Max(S(50),fullListHeight-height);
            list.SetSizeAndPosition(new(list.Size.X,lastListHeight),list.Position);
            var position=list.Position+new Vector2(0,lastListHeight+S(4));
            if(details==null||!ReferenceEquals(details.Parent,dialog))
            {
                details=DWLabel.Generate("Select a project to see its full name.","ACM.DesignDetails",position,new(list.Size.X,height-S(4)),0,ColorHelper.ForeColor,false,FontSize.Normal,TextAlignment.Left,dialog,string.Empty,Side.Right);
                details.Visible=true;
            }
            details.SetSizeAndPosition(new(list.Size.X,height-S(4)),position);RefreshDetails();
        }
        internal void RefreshDetails()
        {
            if(details==null)return;
            var selected=dialog.DesignsList.SelectedItems.OfType<Design>().FirstOrDefault();
            details.Text=selected==null||dialog.Galaxy==null||dialog.Empire==null?"Select a project to see its full name.":selected.Name+"\nSeries: "+Text(selected,dialog.Galaxy,dialog.Empire)+" | "+selected.GetShipHullName(dialog.Galaxy);
        }
    }
}
