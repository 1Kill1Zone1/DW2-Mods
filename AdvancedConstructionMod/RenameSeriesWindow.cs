namespace AdvancedConstructionMod;

internal sealed class RenameSeriesWindow : NativeWindow
{
    internal RenameSeriesWindow(EditorSession session,Action changed):base("AdvancedConstructionRename","RENAME SERIES",600,230)
    {
        Campaign=session.Galaxy;
        var input=TextBox("ACM.RenameName",S(12),S(48),Panel.Size.X-S(24),session.ClassName);
        Status=Label("ACM.RenameStatus","Renames this series for every assigned design. Changes remain staged until Save / Apply.",S(12),S(86),Panel.Size.X-S(24),S(64));
        Button("ACM.RenameApply","Rename",S(12),Panel.Size.Y-S(40),S(120),()=>
        {
            if(!ReferenceEquals(Active,this))return;
            if(!session.NameClass(input.Text,session.ClassId,out var error)){Message(error,true);return;}
            changed();Close();
        });
        Button("ACM.RenameCancel","Cancel",Panel.Size.X-S(132),Panel.Size.Y-S(40),S(120),Close);
    }
}
