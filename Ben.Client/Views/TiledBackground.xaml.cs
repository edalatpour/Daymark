namespace Ben.Views;

public partial class TiledBackground : ContentView
{
	public static readonly BindableProperty ImageSourceProperty =
		BindableProperty.Create(
			nameof(ImageSource),
			typeof(ImageSource),
			typeof(TiledBackground),
			default(ImageSource),
			propertyChanged: (b, o, n) => ((TiledBackground)b).RebuildTiles());

	public static readonly BindableProperty TileSizeProperty =
		BindableProperty.Create(
			nameof(TileSize),
			typeof(double),
			typeof(TiledBackground),
			512.0,
			propertyChanged: (b, o, n) => ((TiledBackground)b).RebuildTiles());

	public static readonly BindableProperty TileOpacityProperty =
		BindableProperty.Create(
			nameof(TileOpacity),
			typeof(double),
			typeof(TiledBackground),
			1.0,
			propertyChanged: (b, o, n) => ((TiledBackground)b).RebuildTiles());

	public ImageSource ImageSource
	{
		get => (ImageSource)GetValue(ImageSourceProperty);
		set => SetValue(ImageSourceProperty, value);
	}

	public double TileSize
	{
		get => (double)GetValue(TileSizeProperty);
		set => SetValue(TileSizeProperty, value);
	}

	public double TileOpacity
	{
		get => (double)GetValue(TileOpacityProperty);
		set => SetValue(TileOpacityProperty, value);
	}

	private double _lastWidth = -1;
	private double _lastHeight = -1;

	public TiledBackground()
	{
		InitializeComponent();
	}

	protected override void OnSizeAllocated(double width, double height)
	{
		base.OnSizeAllocated(width, height);

		if (width <= 0 || height <= 0)
			return;

		// Only rebuild if size actually changed
		if (Math.Abs(width - _lastWidth) > 1 || Math.Abs(height - _lastHeight) > 1)
		{
			_lastWidth = width;
			_lastHeight = height;
			RebuildTiles();
		}
	}

	private void RebuildTiles()
	{
		if (RootGrid == null || ImageSource == null || TileSize <= 0)
			return;

		double width = _lastWidth > 0 ? _lastWidth : Width;
		double height = _lastHeight > 0 ? _lastHeight : Height;

		if (width <= 0 || height <= 0)
			return;

		RootGrid.Children.Clear();
		RootGrid.RowDefinitions.Clear();
		RootGrid.ColumnDefinitions.Clear();

		int cols = Math.Max(1, (int)Math.Ceiling(width / TileSize));
		int rows = Math.Max(1, (int)Math.Ceiling(height / TileSize));

		for (int c = 0; c < cols; c++)
			RootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TileSize) });

		for (int r = 0; r < rows; r++)
			RootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TileSize) });

		for (int r = 0; r < rows; r++)
		{
			for (int c = 0; c < cols; c++)
			{
				var img = new Image
				{
					Source = ImageSource,
					Aspect = Aspect.AspectFill,
					Opacity = TileOpacity,
					InputTransparent = true
				};

				RootGrid.Add(img, c, r);
			}
		}
	}
}
