namespace Aibell.Platform.Linux;

internal static class MprisIntrospection
{
    public static ReadOnlyMemory<byte> Xml { get; } = """
        <interface name="org.mpris.MediaPlayer2">
          <method name="Raise"/><method name="Quit"/>
          <property name="CanQuit" type="b" access="read"/>
          <property name="CanRaise" type="b" access="read"/>
          <property name="HasTrackList" type="b" access="read"/>
          <property name="Identity" type="s" access="read"/>
          <property name="DesktopEntry" type="s" access="read"/>
          <property name="SupportedUriSchemes" type="as" access="read"/>
          <property name="SupportedMimeTypes" type="as" access="read"/>
        </interface>
        <interface name="org.mpris.MediaPlayer2.Player">
          <method name="Play"/><method name="Pause"/><method name="PlayPause"/>
          <method name="Stop"/><method name="Next"/><method name="Previous"/>
          <method name="Seek"><arg name="Offset" type="x" direction="in"/></method>
          <method name="SetPosition"><arg name="TrackId" type="o" direction="in"/><arg name="Position" type="x" direction="in"/></method>
          <method name="OpenUri"><arg name="Uri" type="s" direction="in"/></method>
          <signal name="Seeked"><arg name="Position" type="x"/></signal>
          <property name="PlaybackStatus" type="s" access="read"/>
          <property name="LoopStatus" type="s" access="readwrite"/>
          <property name="Rate" type="d" access="readwrite"/>
          <property name="Shuffle" type="b" access="readwrite"/>
          <property name="Metadata" type="a{sv}" access="read"/>
          <property name="Volume" type="d" access="readwrite"/>
          <property name="Position" type="x" access="read"><annotation name="org.freedesktop.DBus.Property.EmitsChangedSignal" value="false"/></property>
          <property name="MinimumRate" type="d" access="read"/><property name="MaximumRate" type="d" access="read"/>
          <property name="CanGoNext" type="b" access="read"/><property name="CanGoPrevious" type="b" access="read"/>
          <property name="CanPlay" type="b" access="read"/><property name="CanPause" type="b" access="read"/>
          <property name="CanSeek" type="b" access="read"/>
          <property name="CanControl" type="b" access="read"><annotation name="org.freedesktop.DBus.Property.EmitsChangedSignal" value="false"/></property>
        </interface>
        <interface name="org.freedesktop.DBus.Properties">
          <method name="Get"><arg type="s" direction="in"/><arg type="s" direction="in"/><arg type="v" direction="out"/></method>
          <method name="GetAll"><arg type="s" direction="in"/><arg type="a{sv}" direction="out"/></method>
          <method name="Set"><arg type="s" direction="in"/><arg type="s" direction="in"/><arg type="v" direction="in"/></method>
          <signal name="PropertiesChanged"><arg type="s"/><arg type="a{sv}"/><arg type="as"/></signal>
        </interface>
        """u8.ToArray();
}
